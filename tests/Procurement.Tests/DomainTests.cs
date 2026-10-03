using Procurement.Domain;

namespace Procurement.Tests;

public sealed class DomainTests
{
    private static readonly Guid Factory = Guid.NewGuid();
    private static DraftLine Line(Guid? sku = null, string size = "M", int quantity = 100) =>
        new(sku ?? Guid.NewGuid(), new("S001", "蓝色", size), PieceQuantity.From(quantity));
    private static PurchaseOrder Create(params DraftLine[] lines) =>
        PurchaseOrder.CreateDraft(Factory, new(2026, 10, 31), "buyer", DateTimeOffset.UtcNow, lines);

    [Fact]
    public void Different_sizes_are_distinct_items_and_draft_has_stable_identifiers()
    {
        var order = Create(Line(), Line(size: "L", quantity: 50));
        Assert.Equal("Draft", order.Status);
        Assert.Equal(1, order.Revision);
        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(2, order.Lines.Select(x => x.Id).Distinct().Count());
        Assert.Equal(150, order.Lines.Sum(x => x.Quantity.Value));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)]
    public void Non_positive_quantities_are_rejected(int quantity) =>
        Assert.Throws<DomainRuleViolation>(() => PieceQuantity.From(quantity));
    [Fact]
    public void Duplicate_sku_is_rejected_even_with_different_quantities()
    {
        var sku = Guid.NewGuid();
        Assert.Throws<DomainRuleViolation>(() => Create(Line(sku), Line(sku, quantity: 50)));
    }
    [Fact]
    public void Empty_order_is_rejected() => Assert.Throws<DomainRuleViolation>(() => Create());
    [Fact]
    public void Default_quantity_cannot_bypass_creation_rules() =>
        Assert.Throws<DomainRuleViolation>(() => Create(new DraftLine(Guid.NewGuid(), new("S001", "蓝色", "M"), default)));
    [Fact]
    public void Missing_factory_is_rejected() => Assert.Throws<DomainRuleViolation>(() =>
        PurchaseOrder.CreateDraft(Guid.Empty, new(2026, 10, 31), "buyer", DateTimeOffset.UtcNow, [Line()]));
    [Fact]
    public void Missing_date_is_rejected() => Assert.Throws<DomainRuleViolation>(() =>
        PurchaseOrder.CreateDraft(Factory, default, "buyer", DateTimeOffset.UtcNow, [Line()]));
    [Fact]
    public void Incomplete_product_snapshot_is_rejected() =>
        Assert.Throws<DomainRuleViolation>(() => new ProductSnapshot("S001", "", "M"));
    [Fact]
    public void Lines_cannot_be_modified_through_the_exposed_collection()
    {
        var order = Create(Line());
        Assert.Throws<NotSupportedException>(() => ((IList<PurchaseOrderLine>)order.Lines).Clear());
    }
}
