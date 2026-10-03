using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    private static CreatedOrder ReadResult(HttpRequestResult record, string hash)
    {
        if (record.Hash != hash) throw new UseCaseFailure(FailureKind.Conflict, "同一幂等键已用于不同内容，请核对操作。");
        return JsonSerializer.Deserialize<CreatedOrder>(record.ResponseJson)
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
                new OrderLineDetail(x.Id, x.SkuId, x.Product.Style, x.Product.Color, x.Product.Size, x.Quantity.Value)).ToArray());
    }
}
