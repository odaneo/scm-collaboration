using Microsoft.EntityFrameworkCore;
using Procurement.Application;
using Procurement.Domain;
using Scm.IntegrationContracts;
using Scm.Messaging;

namespace Procurement.Persistence;

public sealed class ProductionTaskLink
{
    public Guid OrderId { get; set; }
    public int AcceptedOrderVersion { get; set; }
    public Guid FactoryId { get; set; }
    public Guid DecisionId { get; set; }
    public string AcceptedContentHash { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public Guid? TaskId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ErrorCode { get; set; }
    public static ProductionTaskLink Pending(PurchaseOrderAcceptedV1 payload, DateTimeOffset now) => new()
    {
        OrderId = payload.OrderId, AcceptedOrderVersion = payload.AcceptedOrderVersion, FactoryId = payload.FactoryId,
        DecisionId = payload.DecisionId, AcceptedContentHash = payload.ContentHash(), UpdatedAt = now
    };
}
public sealed class ProcurementMessageHandler(ProcurementDbContext db) : IIntegrationMessageHandler
{
    public async Task<string?> Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.SourceService != "Production") throw new InvalidMessage("UnexpectedSource");
        Guid orderId, factoryId, decisionId; int version; string hash; Guid? taskId = null; string? error = null;
        if (envelope.EventType == nameof(ProductionTaskCreatedV1))
        {
            var payload = envelope.Read<ProductionTaskCreatedV1>();
            if (payload.TaskId == Guid.Empty || envelope.FactId != payload.TaskId || payload.CreatedAtUtc == default)
                throw new InvalidMessage("InvalidTaskReceipt");
            (orderId, factoryId, decisionId, version, hash, taskId) = (payload.OrderId, payload.FactoryId, payload.DecisionId,
                payload.AcceptedOrderVersion, payload.AcceptedContentHash, payload.TaskId);
        }
        else if (envelope.EventType == nameof(ProductionTaskCreationFailedV1))
        {
            var payload = envelope.Read<ProductionTaskCreationFailedV1>();
            if (payload.ErrorCode is not ("AcceptedContentConflict" or "InvalidAcceptedOrder")) throw new InvalidMessage("InvalidFailureCode");
            (orderId, factoryId, decisionId, version, hash, error) = (payload.OrderId, payload.FactoryId, payload.DecisionId,
                payload.AcceptedOrderVersion, payload.AcceptedContentHash, payload.ErrorCode);
        }
        else throw new InvalidMessage("UnexpectedEventType");
        if (orderId == Guid.Empty || factoryId == Guid.Empty || decisionId == Guid.Empty || version < 1 || hash?.Length != 64)
            throw new InvalidMessage("InvalidReceiptIdentity");
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new MissingPrerequisite();
        var accepted = await db.Versions.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId && x.Version == version, ct)
            ?? throw new MissingPrerequisite();
        if (order.Status != "Accepted" || order.AcceptedOrderVersion != version || accepted.Status != "Accepted" ||
            accepted.FactoryId != factoryId || accepted.DecisionId != decisionId) throw new InvalidMessage("ReceiptAcceptanceMismatch");
        var link = await db.Set<ProductionTaskLink>().FromSqlInterpolated($"""
            SELECT * FROM production_task_links WHERE "OrderId" = {orderId} FOR UPDATE
            """).SingleOrDefaultAsync(ct) ?? throw new MissingPrerequisite();
        if (link.AcceptedContentHash != hash || link.DecisionId != decisionId || link.FactoryId != factoryId || link.AcceptedOrderVersion != version)
            throw new InvalidMessage("ReceiptContentMismatch");
        if (link.Status == "Created")
        {
            if (taskId is { } duplicate && link.TaskId != duplicate) throw new InvalidMessage("ReceiptTaskConflict");
            return null;
        }
        link.Status = taskId is null ? "Blocked" : "Created"; link.TaskId = taskId;
        link.ErrorCode = error; link.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }
}
public static class ProductionTaskBackfill
{
    public static async Task<int> Run(ProcurementDbContext db, bool apply, CancellationToken ct = default)
    {
        var candidates = await db.Versions.AsNoTracking().Where(x => x.Status == "Accepted")
            .Select(x => new { x.OrderId, x.Version, x.DecisionId }).ToListAsync(ct);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(candidates, ContractJson.Options));
        if (!apply) return 0;
        var count = 0;
        foreach (var candidate in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var version = await db.Versions.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.OrderId == candidate.OrderId && x.Version == candidate.Version, ct);
            var order = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == version.OrderId, ct);
            if (order.Status != "Accepted" || order.AcceptedOrderVersion != version.Version || version.DecisionId is null ||
                version.ResolvedRevision is null || version.ResolvedAt is null) throw new InvalidOperationException("历史接单记录不完整，停止补录。");
            var fact = new OrderVersionAccepted(version.OrderId, version.Version, version.FactoryId, version.DecisionId.Value,
                version.ResolvedRevision.Value, version.ResolvedAt.Value);
            var envelope = AcceptedOrderEvents.From(fact, version); var payload = envelope.Read<PurchaseOrderAcceptedV1>();
            var row = OutboxMessage.From(envelope, payload.ContentHash());
            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO outbox_messages ("MessageId", "EventType", "FactId", "EnvelopeJson", "ContentHash", "EnqueuedAt", "Attempts", "NextAttemptAt")
                VALUES ({row.MessageId}, {row.EventType}, {row.FactId}, {row.EnvelopeJson}, {row.ContentHash}, {row.EnqueuedAt}, 0, {row.NextAttemptAt})
                ON CONFLICT ("EventType", "FactId") DO NOTHING
                """, ct);
            if (claimed == 1)
            {
                db.Set<ProductionTaskLink>().Add(ProductionTaskLink.Pending(payload, DateTimeOffset.UtcNow));
                db.Audit.Add(new(Guid.NewGuid(), order.Id, "ProductionTaskBackfilled", "development-backfill", order.FactoryId,
                    DateTimeOffset.UtcNow, order.Revision, version.Version));
                await db.SaveChangesAsync(ct); count++;
            }
            else
            {
                var previous = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(x => x.EventType == row.EventType && x.FactId == row.FactId, ct);
                if (previous.ContentHash != row.ContentHash) throw new InvalidOperationException("历史事实与原事件内容冲突，停止补录。");
            }
            await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
        }
        Console.WriteLine($"新增任务建立请求：{count}"); return count;
    }
}
