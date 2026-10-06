using Procurement.Domain;
using Scm.IntegrationContracts;

namespace Procurement.Application;

public static class AcceptedOrderEvents
{
    public static MessageEnvelope From(OrderVersionAccepted fact, SubmittedOrderVersion version)
    {
        if (version.Status != "Accepted" || version.DecisionId != fact.DecisionId || version.OrderId != fact.OrderId ||
            version.Version != fact.Version || version.FactoryId != fact.FactoryId || version.Lines.Count == 0)
            throw new InvalidOperationException("接单事实与完整内容快照不匹配。");
        var payload = new PurchaseOrderAcceptedV1(fact.OrderId, fact.Version, fact.FactoryId, version.FactoryName,
            fact.DecisionId, fact.Revision, fact.OccurredAtUtc, version.DeliveryDate,
            version.Lines.Select(x => new AcceptedLine(x.LineId, x.SkuId, x.Product.Style, x.Product.Color,
                x.Product.Size, x.Quantity.Value)).ToArray()).Normalize();
        return MessageEnvelope.Create(payload, fact.DecisionId, "Procurement", fact.Revision, fact.OccurredAtUtc);
    }
}
