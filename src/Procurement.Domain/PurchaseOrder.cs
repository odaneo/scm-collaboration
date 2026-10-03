namespace Procurement.Domain;

public sealed class DomainRuleViolation(string message) : Exception(message);

public readonly record struct PieceQuantity
{
    public int Value { get; }
    private PieceQuantity(int value) => Value = value;
    public static PieceQuantity From(int value) => value > 0
        ? new(value) : throw new DomainRuleViolation("数量必须是正整数，单位为件。");
}

public sealed record ProductSnapshot
{
    public string Style { get; private set; } = "";
    public string Color { get; private set; } = "";
    public string Size { get; private set; } = "";
    private ProductSnapshot() { }
    public ProductSnapshot(string style, string color, string size)
    {
        if (new[] { style, color, size }.Any(string.IsNullOrWhiteSpace))
            throw new DomainRuleViolation("商品的款式、颜色和尺码必须完整。");
        (Style, Color, Size) = (style, color, size);
    }
}

public sealed record DraftLine(Guid SkuId, ProductSnapshot Product, PieceQuantity Quantity);

public sealed class PurchaseOrderLine
{
    public Guid Id { get; private set; }
    public Guid SkuId { get; private set; }
    public ProductSnapshot Product { get; private set; } = null!;
    public PieceQuantity Quantity { get; private set; }
    private PurchaseOrderLine() { }
    internal PurchaseOrderLine(DraftLine line)
    {
        if (line.SkuId == Guid.Empty || line.Product is null)
            throw new DomainRuleViolation("订单明细必须指定商品。");
        Id = Guid.NewGuid();
        SkuId = line.SkuId;
        Product = line.Product;
        Quantity = PieceQuantity.From(line.Quantity.Value);
    }
}

public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];
    public Guid Id { get; private set; }
    public Guid FactoryId { get; private set; }
    public DateOnly DeliveryDate { get; private set; }
    public string Status { get; private set; } = "Draft";
    public int Revision { get; private set; } = 1;
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<PurchaseOrderLine> Lines => _lines.AsReadOnly();
    private PurchaseOrder() { }

    public static PurchaseOrder CreateDraft(Guid factoryId, DateOnly deliveryDate,
        string createdBy, DateTimeOffset createdAt, IEnumerable<DraftLine> lines)
    {
        if (factoryId == Guid.Empty || deliveryDate == default || string.IsNullOrWhiteSpace(createdBy))
            throw new DomainRuleViolation("工厂、交期和创建人不能为空。");
        ArgumentNullException.ThrowIfNull(lines);
        var order = new PurchaseOrder
        {
            Id = Guid.NewGuid(), FactoryId = factoryId, DeliveryDate = deliveryDate,
            CreatedBy = createdBy, CreatedAt = createdAt.ToUniversalTime()
        };
        foreach (var line in lines) order.AddLine(line);
        if (order._lines.Count == 0) throw new DomainRuleViolation("订单至少需要一个明细。");
        return order;
    }

    // 1A 的明细只在创建行为内添加，避免保存空草稿或暴露独立明细 CRUD。
    private void AddLine(DraftLine line)
    {
        if (_lines.Any(x => x.SkuId == line.SkuId))
            throw new DomainRuleViolation("同一订单不能包含重复 SKU。");
        _lines.Add(new PurchaseOrderLine(line));
    }
}
