namespace Procurement.Domain;

public sealed class DomainRuleViolation(string message) : Exception(message);
public sealed class DomainConflict(string message) : Exception(message);

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
    internal void ChangeQuantity(PieceQuantity quantity) => Quantity = PieceQuantity.From(quantity.Value);
}

public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];
    public Guid Id { get; private set; }
    public Guid FactoryId { get; private set; }
    public DateOnly DeliveryDate { get; private set; }
    public string Status { get; private set; } = "Draft";
    public int Revision { get; private set; } = 1;
    public int LastSubmittedVersion { get; private set; }
    public int? AcceptedOrderVersion { get; private set; }
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
        foreach (var line in ValidateLines(lines)) order.AddLine(line);
        return order;
    }

    public bool UpdateDraft(Guid factoryId, DateOnly deliveryDate, IEnumerable<DraftLine> lines)
    {
        RequireEditable();
        if (factoryId == Guid.Empty || deliveryDate == default) throw new DomainRuleViolation("工厂和交期不能为空。");
        var input = ValidateLines(lines);
        if (FactoryId == factoryId && DeliveryDate == deliveryDate && input.Count == _lines.Count &&
            input.All(x => _lines.Any(y => y.SkuId == x.SkuId && y.Quantity == x.Quantity))) return false;
        var nextRevision = checked(Revision + 1);
        var retained = input.Select(line =>
            _lines.SingleOrDefault(x => x.SkuId == line.SkuId) ?? new PurchaseOrderLine(line)).ToList();
        foreach (var line in retained) line.ChangeQuantity(input.Single(x => x.SkuId == line.SkuId).Quantity);
        _lines.Clear();
        _lines.AddRange(retained);
        FactoryId = factoryId;
        DeliveryDate = deliveryDate;
        Status = "Draft";
        Revision = nextRevision;
        return true;
    }
    public SubmittedOrderVersion Submit(string factoryName, string subject, DateTimeOffset at, DateOnly businessToday)
    {
        RequireEditable();
        if (DeliveryDate < businessToday) throw new DomainRuleViolation("提交交期不能早于上海业务当天。");
        if (string.IsNullOrWhiteSpace(factoryName) || string.IsNullOrWhiteSpace(subject))
            throw new DomainRuleViolation("提交工厂和提交人不能为空。");
        var nextRevision = checked(Revision + 1);
        var nextVersion = checked(LastSubmittedVersion + 1);
        var submission = new SubmittedOrderVersion(this, nextVersion, nextRevision, factoryName, subject, at);
        LastSubmittedVersion = nextVersion;
        Revision = nextRevision;
        Status = "Submitted";
        return submission;
    }
    public void WithdrawSubmission(SubmittedOrderVersion version, string subject, DateTimeOffset at, string? reason)
    {
        RequirePending(version);
        var text = RequiredReason(reason);
        var nextRevision = checked(Revision + 1);
        version.Resolve("Withdrawn", subject, at, nextRevision, text);
        Revision = nextRevision;
        Status = "Draft";
    }
    public OrderVersionAccepted AcceptVersion(SubmittedOrderVersion version, Guid factoryId, string subject, DateTimeOffset at)
    {
        RequireFactoryVersion(version, factoryId);
        var nextRevision = checked(Revision + 1);
        version.Resolve("Accepted", subject, at, nextRevision, null);
        Revision = nextRevision;
        Status = "Accepted";
        AcceptedOrderVersion = version.Version;
        return new(Id, version.Version, version.FactoryId, version.DecisionId!.Value, Revision, version.ResolvedAt!.Value);
    }
    public void RejectVersion(SubmittedOrderVersion version, Guid factoryId, string subject, DateTimeOffset at, string? reason)
    {
        RequireFactoryVersion(version, factoryId);
        var text = RequiredReason(reason);
        var nextRevision = checked(Revision + 1);
        version.Resolve("Rejected", subject, at, nextRevision, text);
        Revision = nextRevision;
        Status = "Rejected";
    }
    private void RequireEditable()
    {
        if (Status is not ("Draft" or "Rejected"))
            throw new DomainConflict("只有草稿或已拒绝订单可以编辑、提交；待确认订单须先撤回，接单后不可直接修改。");
    }
    private void RequirePending(SubmittedOrderVersion version)
    {
        if (Status != "Submitted" || version.OrderId != Id || version.Version != LastSubmittedVersion || version.Status != "Pending")
            throw new DomainConflict("该版本不再待工厂确认，请刷新查看处理结果。");
    }
    private void RequireFactoryVersion(SubmittedOrderVersion version, Guid factoryId)
    {
        RequirePending(version);
        if (factoryId == Guid.Empty || factoryId != version.FactoryId)
            throw new DomainConflict("只有该提交版本所属工厂可以决定接单。");
    }
    private static string RequiredReason(string? reason)
    {
        var text = reason?.Trim();
        return text is { Length: >= 1 and <= 500 } ? text
            : throw new DomainRuleViolation("拒绝或撤回必须填写 1–500 字符的原因。");
    }
    private static List<DraftLine> ValidateLines(IEnumerable<DraftLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var input = lines.ToList();
        if (input.Count is < 1 or > 100) throw new DomainRuleViolation("订单必须包含 1–100 个明细。");
        if (input.Any(x => x is null || x.SkuId == Guid.Empty || x.Product is null))
            throw new DomainRuleViolation("订单明细必须指定商品。");
        if (input.Select(x => x.SkuId).Distinct().Count() != input.Count)
            throw new DomainRuleViolation("同一订单不能包含重复 SKU。");
        foreach (var line in input) PieceQuantity.From(line.Quantity.Value);
        return input;
    }
    // 明细始终经订单行为变更，不提供独立明细 CRUD。
    private void AddLine(DraftLine line)
    {
        if (_lines.Any(x => x.SkuId == line.SkuId))
            throw new DomainRuleViolation("同一订单不能包含重复 SKU。");
        _lines.Add(new PurchaseOrderLine(line));
    }
}
