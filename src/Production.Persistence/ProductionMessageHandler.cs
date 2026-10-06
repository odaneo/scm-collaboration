using Microsoft.EntityFrameworkCore;
using Production.Application;
using Production.Domain;
using Scm.IntegrationContracts;
using Scm.Messaging;

namespace Production.Persistence;

public sealed class ProductionMessageHandler(ProductionDbContext db, ProductionService service) : IIntegrationMessageHandler
{
    public async Task<string?> Handle(MessageEnvelope envelope, CancellationToken ct)
    {
        if (envelope.SourceService != "Procurement" || envelope.EventType != nameof(PurchaseOrderAcceptedV1))
            throw new InvalidMessage("UnexpectedSourceOrType");
        var payload = envelope.Read<PurchaseOrderAcceptedV1>();
        if (payload.OrderId == Guid.Empty || payload.FactoryId == Guid.Empty || payload.DecisionId == Guid.Empty ||
            payload.AcceptedOrderVersion < 1 || payload.DecisionId != envelope.FactId || payload.AcceptedRevision != envelope.SourceRevision ||
            payload.AcceptedAtUtc != envelope.OccurredAtUtc || payload.Lines is null || payload.Lines.Any(x => x is null))
            throw new InvalidMessage("InvalidAcceptanceIdentity");
        var hash = payload.ContentHash();
        try
        {
            var input = new ConfirmedOrder(payload.OrderId, payload.AcceptedOrderVersion, payload.FactoryId, payload.FactoryName,
                payload.DecisionId, payload.AcceptedRevision, payload.AcceptedAtUtc, payload.DeliveryDate,
                payload.Lines.Select(x => new ConfirmedLine(x.LineId, x.SkuId, x.Style, x.Color, x.Size, x.Quantity)).ToArray());
            var (task, created) = await service.Initialize(input, hash, ct);
            if (created)
            {
                db.Set<ProductionAudit>().Add(new(Guid.NewGuid(), task.Id, task.OrderId, task.DecisionId, "TaskCreated", task.CreatedAtUtc));
                var receipt = new ProductionTaskCreatedV1(task.OrderId, task.AcceptedOrderVersion, task.FactoryId, task.DecisionId,
                    hash, task.Id, task.CreatedAtUtc);
                db.Set<OutboxMessage>().Add(OutboxMessage.From(MessageEnvelope.Create(receipt, task.Id, "Production", 1, task.CreatedAtUtc, envelope)));
            }
            return null;
        }
        catch (Exception ex) when (ex is ProductionRuleViolation or AcceptedContentConflict)
        {
            var code = ex is AcceptedContentConflict ? "AcceptedContentConflict" : "InvalidAcceptedOrder";
            var failed = new ProductionTaskCreationFailedV1(payload.OrderId, payload.AcceptedOrderVersion, payload.FactoryId,
                payload.DecisionId, hash, code);
            var failure = MessageEnvelope.Create(failed, envelope.MessageId, "Production", 1, DateTimeOffset.UtcNow, envelope);
            if (!await db.Set<OutboxMessage>().AnyAsync(x => x.EventType == failure.EventType && x.FactId == failure.FactId, ct))
                db.Set<OutboxMessage>().Add(OutboxMessage.From(failure));
            return code;
        }
    }
}
