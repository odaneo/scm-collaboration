using Production.Domain;
using Procurement.Domain;

namespace Procurement.Tests;

public sealed class TaskDomainTests
{
    private static ConfirmedOrder Order(int quantity = 100) => new(Guid.NewGuid(), 1, Guid.NewGuid(), "合作工厂 A",
        Guid.NewGuid(), 3, DateTimeOffset.UtcNow.AddDays(-3), new(2026, 1, 1),
        [new(Guid.NewGuid(), Guid.NewGuid(), "SHIRT-01", "白色", "M", quantity)]);
    [Fact] public void CreatesImmutableConfirmedSnapshotEvenAfterDeliveryDate()
    {
        var input = Order(); var task = ProductionTask.CreateFromAcceptedOrder(input, DateTimeOffset.UtcNow);
        Assert.Equal(input.DecisionId, task.DecisionId); Assert.Equal(input.Lines[0].LineId, task.Lines[0].LineId);
        Assert.Equal(100, task.Lines[0].ConfirmedQuantity); Assert.Equal(input.DeliveryDate, task.DeliveryDate);
    }
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void RejectsNonPositiveConfirmedQuantity(int quantity) => Assert.Throws<ProductionRuleViolation>(() => ProductionTask.CreateFromAcceptedOrder(Order(quantity), DateTimeOffset.UtcNow));
    [Fact] public void RejectsDuplicateSkuOrLineId()
    {
        var input = Order(); var line = input.Lines[0];
        Assert.Throws<ProductionRuleViolation>(() => ProductionTask.CreateFromAcceptedOrder(input with { Lines = [line, line with { LineId = Guid.NewGuid() }] }, DateTimeOffset.UtcNow));
        Assert.Throws<ProductionRuleViolation>(() => ProductionTask.CreateFromAcceptedOrder(input with { Lines = [line, line with { SkuId = Guid.NewGuid() }] }, DateTimeOffset.UtcNow));
    }
    [Fact] public void RejectsEmptyAndTooManyLines()
    {
        var input = Order();
        Assert.Throws<ProductionRuleViolation>(() => ProductionTask.CreateFromAcceptedOrder(input with { Lines = [] }, DateTimeOffset.UtcNow));
        Assert.Throws<ProductionRuleViolation>(() => ProductionTask.CreateFromAcceptedOrder(input with { Lines = Enumerable.Range(0, 101).Select(_ => Order().Lines[0]).ToArray() }, DateTimeOffset.UtcNow));
    }
    [Fact] public void AcceptBehaviorReturnsInternalFactWithOriginalDecisionIdentity()
    {
        var order = PurchaseOrder.CreateDraft(Guid.NewGuid(), new(2026, 10, 31), "buyer", DateTimeOffset.UtcNow,
            [new DraftLine(Guid.NewGuid(), new("STYLE", "白色", "M"), PieceQuantity.From(10))]);
        var version = order.Submit("工厂 A", "buyer", DateTimeOffset.UtcNow, new(2026, 10, 5));
        var fact = order.AcceptVersion(version, order.FactoryId, "factory-a", DateTimeOffset.UtcNow);
        Assert.Equal(version.DecisionId, fact.DecisionId); Assert.Equal(order.Id, fact.OrderId); Assert.Equal(order.Revision, fact.Revision);
        Assert.Throws<DomainConflict>(() => order.AcceptVersion(version, order.FactoryId, "factory-a", DateTimeOffset.UtcNow));
    }
}
