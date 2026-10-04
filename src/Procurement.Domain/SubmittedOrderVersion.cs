namespace Procurement.Domain;

public sealed class SubmittedOrderLine
{
    public Guid LineId { get; private set; }
    public Guid SkuId { get; private set; }
    public ProductSnapshot Product { get; private set; } = null!;
    public PieceQuantity Quantity { get; private set; }
    private SubmittedOrderLine() { }
    internal SubmittedOrderLine(PurchaseOrderLine line)
    {
        LineId = line.Id;
        SkuId = line.SkuId;
        Product = new(line.Product.Style, line.Product.Color, line.Product.Size);
        Quantity = line.Quantity;
    }
}

// 内容快照冻结；处理状态只能由 PurchaseOrder 的业务行为推进。
public sealed class SubmittedOrderVersion
{
    private readonly List<SubmittedOrderLine> _lines = [];
    public Guid OrderId { get; private set; }
    public int Version { get; private set; }
    public int SubmittedRevision { get; private set; }
    public Guid FactoryId { get; private set; }
    public string FactoryName { get; private set; } = "";
    public DateOnly DeliveryDate { get; private set; }
    public string SubmittedBy { get; private set; } = "";
    public DateTimeOffset SubmittedAt { get; private set; }
    public string Status { get; private set; } = "Pending";
    public Guid? DecisionId { get; private set; }
    public string? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public int? ResolvedRevision { get; private set; }
    public string? Reason { get; private set; }
    public IReadOnlyList<SubmittedOrderLine> Lines => _lines.AsReadOnly();
    private SubmittedOrderVersion() { }
    internal SubmittedOrderVersion(PurchaseOrder order, int version, int revision, string factoryName, string subject, DateTimeOffset at)
    {
        OrderId = order.Id;
        Version = version;
        SubmittedRevision = revision;
        FactoryId = order.FactoryId;
        FactoryName = factoryName;
        DeliveryDate = order.DeliveryDate;
        SubmittedBy = subject;
        SubmittedAt = at.ToUniversalTime();
        _lines.AddRange(order.Lines.Select(x => new SubmittedOrderLine(x)));
    }
    internal void Resolve(string status, string subject, DateTimeOffset at, int revision, string? reason)
    {
        if (Status != "Pending") throw new DomainConflict("提交版本已经处理，不能覆盖原决定。");
        if (string.IsNullOrWhiteSpace(subject)) throw new DomainRuleViolation("处理人不能为空。");
        Status = status;
        DecisionId = status == "Withdrawn" ? null : Guid.NewGuid();
        ResolvedBy = subject;
        ResolvedAt = at.ToUniversalTime();
        ResolvedRevision = revision;
        Reason = reason;
    }
}
