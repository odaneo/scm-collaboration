using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Procurement.Application;
using Procurement.Domain;

namespace Procurement.Persistence;

public sealed class ProcurementStore(ProcurementDbContext db) : IProcurementStore
{
    public async Task<IReadOnlyList<FactoryOption>> GetFactories(CancellationToken ct) =>
        await db.Factories.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name)
            .Select(x => new FactoryOption(x.Id, x.Name)).ToListAsync(ct);
    public async Task<IReadOnlyList<SkuOption>> GetSkus(CancellationToken ct) =>
        await db.Skus.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Style).ThenBy(x => x.Color).ThenBy(x => x.Size)
            .Select(x => new SkuOption(x.Id, x.Style, x.Color, x.Size)).ToListAsync(ct);

    public async Task<CreatedOrder?> FindRequest(string subject, string key, string hash, CancellationToken ct)
    {
        // ponytail: 1A 保留幂等记录不自动过期；长期运行时增加明确的保留与归档策略。
        var previous = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SubjectId == subject && x.Operation == "CreateDraft" && x.Key == key, ct);
        return previous is null ? null : ReadResult(previous, hash);
    }

    public async Task<CreatedOrder> SaveDraft(PurchaseOrder order, string key, string hash, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // 唯一键在数据库中仲裁：并发请求会等待持有者提交或回滚。未完成记录绝不单独提交。
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO http_request_results ("SubjectId", "Operation", "Key", "Hash", "ResponseJson")
            VALUES ({order.CreatedBy}, 'CreateDraft', {key}, {hash}, '')
            ON CONFLICT ("SubjectId", "Operation", "Key") DO NOTHING
            """, ct);
        var record = await db.Requests.SingleAsync(x => x.SubjectId == order.CreatedBy &&
            x.Operation == "CreateDraft" && x.Key == key, ct);
        if (claimed == 0) return ReadResult(record, hash);

        var response = new CreatedOrder(order.Id, order.Status, order.Revision, order.CreatedAt);
        db.Orders.Add(order);
        db.Audit.Add(new(Guid.NewGuid(), order.Id, "DraftCreated", order.CreatedBy, order.FactoryId, order.CreatedAt));
        record.ResponseJson = JsonSerializer.Serialize(response);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    private static CreatedOrder ReadResult(HttpRequestResult record, string hash) => ReadResult<CreatedOrder>(record, hash);
    private static T ReadResult<T>(HttpRequestResult record, string hash) where T : class
    {
        if (record.Hash != hash) throw new UseCaseFailure(FailureKind.Conflict, "同一幂等键已用于不同内容，请核对操作。");
        return JsonSerializer.Deserialize<T>(record.ResponseJson)
            ?? throw new InvalidOperationException("幂等结果数据损坏。");
    }

    public async Task<OrderPage> ListOrders(int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await (from order in query
                           join factory in db.Factories on order.FactoryId equals factory.Id
                           orderby order.CreatedAt descending, order.Id descending
                           select new OrderSummary(order.Id, factory.Name, order.DeliveryDate, order.Status,
                               order.Revision, order.CreatedAt)).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task<OrderDetail?> GetOrder(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null) return null;
        var name = await db.Factories.Where(x => x.Id == order.FactoryId).Select(x => x.Name).SingleAsync(ct);
        return new(order.Id, order.FactoryId, name, order.DeliveryDate, order.Status, order.Revision,
            order.CreatedBy, order.CreatedAt, order.Lines.Select(x =>
                new OrderLineDetail(x.Id, x.SkuId, x.Product.Style, x.Product.Color, x.Product.Size, x.Quantity.Value)).ToArray(),
            order.LastSubmittedVersion, order.AcceptedOrderVersion);
    }

    // 应用层选择领域行为；这里把它与请求仲裁、并发更新、审计放进同一采购事务。
    public async Task<OrderOperationResult> ChangeOrder(OrderChange change,
        Func<PurchaseOrder, SubmittedOrderVersion?, CancellationToken, Task<OrderMutation>> apply, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO http_request_results ("SubjectId", "Operation", "Key", "Hash", "ResponseJson")
            VALUES ({change.Actor.SubjectId}, {change.Operation}, {change.Key}, {change.Hash}, '')
            ON CONFLICT ("SubjectId", "Operation", "Key") DO NOTHING
            """, ct);
        var record = await db.Requests.SingleAsync(x => x.SubjectId == change.Actor.SubjectId &&
            x.Operation == change.Operation && x.Key == change.Key, ct);
        var order = await db.Orders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == change.OrderId, ct)
            ?? throw MissingOrder();
        SubmittedOrderVersion? version = null;
        if (change.Version is { } number)
        {
            var query = db.Versions.Where(x => x.OrderId == change.OrderId && x.Version == number);
            if (change.Actor.Role == Roles.Factory) query = query.Where(x => x.FactoryId == change.Actor.FactoryId);
            version = await query.SingleOrDefaultAsync(ct) ?? throw MissingOrder();
        }
        // 先验证目标可见性，再重放；重放不能被后来的修订号或目录停用打断。
        if (claimed == 0) return ReadResult<OrderOperationResult>(record, change.Hash);
        if (order.Revision != change.ExpectedRevision)
            throw new UseCaseFailure(FailureKind.Conflict, "订单已被其他操作修改，请刷新后确认再操作。");
        var beforeRevision = order.Revision;
        var mutation = await apply(order, version, ct);
        if (mutation.NewVersion is { } submitted)
        {
            db.Versions.Add(submitted);
            version = submitted;
        }
        var result = new OrderOperationResult(order.Id, version?.Version, order.Status, order.Revision, version?.Status, version?.DecisionId);
        if (order.Revision != beforeRevision)
            db.Audit.Add(new(Guid.NewGuid(), order.Id, change.Operation, change.Actor.SubjectId, order.FactoryId,
                version?.ResolvedAt ?? version?.SubmittedAt ?? change.OccurredAt, order.Revision, version?.Version,
                mutation.Reason, mutation.Changes));
        record.ResponseJson = JsonSerializer.Serialize(result);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new UseCaseFailure(FailureKind.Conflict, "订单发生并发修改，本次操作已回滚，请刷新后确认。");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
            pg.ConstraintName is "PK_order_versions" or "IX_order_versions_OrderId" or "IX_business_audit_OrderId_ResultRevision_Action")
        {
            throw new UseCaseFailure(FailureKind.Conflict, "该订单版本已由另一操作处理，本次操作已回滚。");
        }
        return result;
    }
    public async Task<VersionPage> GetVersions(Guid? factoryId, Guid? orderId, string? status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Versions.AsNoTracking();
        if (factoryId is { } factory) query = query.Where(x => x.FactoryId == factory);
        if (orderId is { } targetOrder) query = query.Where(x => x.OrderId == targetOrder);
        if (status is not null) query = query.Where(x => x.Status == status);
        var total = await query.CountAsync(ct);
        var items = await (from version in query
                           join order in db.Orders on version.OrderId equals order.Id
                           orderby version.SubmittedAt descending, version.OrderId descending, version.Version descending
                           select new VersionSummary(version.OrderId, version.Version, version.FactoryName, version.DeliveryDate,
                               version.Status, version.SubmittedAt, version.Status == "Pending" && order.Status == "Submitted" &&
                               version.Version == order.LastSubmittedVersion ? (int?)order.Revision : null))
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
    public async Task<VersionDetail?> GetVersion(Guid? factoryId, Guid orderId, int number, CancellationToken ct)
    {
        var query = db.Versions.AsNoTracking().Where(x => x.OrderId == orderId && x.Version == number);
        if (factoryId is { } factory) query = query.Where(x => x.FactoryId == factory);
        var version = await query.Include(x => x.Lines).SingleOrDefaultAsync(ct);
        if (version is null) return null;
        var revision = await db.Orders.Where(x => x.Id == orderId && x.Status == "Submitted" &&
            x.LastSubmittedVersion == number && version.Status == "Pending").Select(x => (int?)x.Revision).SingleOrDefaultAsync(ct);
        return new(version.OrderId, version.Version, version.FactoryId, version.FactoryName, version.DeliveryDate,
            version.SubmittedRevision, version.SubmittedBy, version.SubmittedAt, version.Status, version.DecisionId,
            version.ResolvedBy, version.ResolvedAt, version.ResolvedRevision, version.Reason, revision,
            version.Lines.Select(x => new OrderLineDetail(x.LineId, x.SkuId, x.Product.Style, x.Product.Color, x.Product.Size, x.Quantity.Value)).ToArray());
    }
    public async Task<AuditPage> GetAudit(Guid id, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Audit.AsNoTracking().Where(x => x.OrderId == id);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.ResultRevision).ThenByDescending(x => x.Id)
            .Select(x => new AuditDetail(x.Id, x.Action, x.SubjectId, x.OccurredAt, x.ResultRevision, x.OrderVersion, x.Reason, x.Changes))
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }
    private static UseCaseFailure MissingOrder() => new(FailureKind.NotFound, "订单或提交版本不存在或不可见。");
}
