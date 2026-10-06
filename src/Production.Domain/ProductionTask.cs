namespace Production.Domain;

public sealed class ProductionRuleViolation(string message) : Exception(message);
public sealed record ConfirmedLine(Guid LineId, Guid SkuId, string Style, string Color, string Size, int Quantity);
public sealed record ConfirmedOrder(Guid OrderId, int Version, Guid FactoryId, string FactoryName, Guid DecisionId,
    int AcceptedRevision, DateTimeOffset AcceptedAtUtc, DateOnly DeliveryDate, IReadOnlyList<ConfirmedLine> Lines);

public sealed class ProductionTaskLine
{
    public Guid LineId { get; private set; }
    public Guid SkuId { get; private set; }
    public string Style { get; private set; } = "";
    public string Color { get; private set; } = "";
    public string Size { get; private set; } = "";
    public int ConfirmedQuantity { get; private set; }
    private ProductionTaskLine() { }
    internal ProductionTaskLine(ConfirmedLine line) =>
        (LineId, SkuId, Style, Color, Size, ConfirmedQuantity) = (line.LineId, line.SkuId, line.Style, line.Color, line.Size, line.Quantity);
}
public sealed class ProductionTask
{
    private readonly List<ProductionTaskLine> _lines = [];
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public int AcceptedOrderVersion { get; private set; }
    public Guid FactoryId { get; private set; }
    public string FactoryName { get; private set; } = "";
    public Guid DecisionId { get; private set; }
    public int AcceptedRevision { get; private set; }
    public DateTimeOffset AcceptedAtUtc { get; private set; }
    public DateOnly DeliveryDate { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public IReadOnlyList<ProductionTaskLine> Lines => _lines.AsReadOnly();
    private ProductionTask() { }
    public static ProductionTask CreateFromAcceptedOrder(ConfirmedOrder input, DateTimeOffset now)
    {
        if (input.OrderId == Guid.Empty || input.FactoryId == Guid.Empty || input.DecisionId == Guid.Empty || input.Version < 1 ||
            input.AcceptedRevision < 1 || input.AcceptedAtUtc == default || input.DeliveryDate == default ||
            string.IsNullOrWhiteSpace(input.FactoryName) || input.FactoryName.Length > 200)
            throw new ProductionRuleViolation("接单标识、版本、工厂和交期必须完整。");
        if (input.Lines is null || input.Lines.Count is < 1 or > 100 || input.Lines.Any(x => x is null ||
            x.LineId == Guid.Empty || x.SkuId == Guid.Empty || x.Quantity <= 0 || string.IsNullOrWhiteSpace(x.Style) ||
            string.IsNullOrWhiteSpace(x.Color) || string.IsNullOrWhiteSpace(x.Size) || x.Style.Length > 100 || x.Color.Length > 100 || x.Size.Length > 30))
            throw new ProductionRuleViolation("任务须有 1–100 条完整、正整数件数的确认明细。");
        if (input.Lines.Select(x => x.LineId).Distinct().Count() != input.Lines.Count ||
            input.Lines.Select(x => x.SkuId).Distinct().Count() != input.Lines.Count)
            throw new ProductionRuleViolation("任务明细编号和 SKU 不能重复。");
        var task = new ProductionTask
        {
            Id = Guid.NewGuid(), OrderId = input.OrderId, AcceptedOrderVersion = input.Version, FactoryId = input.FactoryId,
            FactoryName = input.FactoryName, DecisionId = input.DecisionId, AcceptedRevision = input.AcceptedRevision,
            AcceptedAtUtc = input.AcceptedAtUtc.ToUniversalTime(), DeliveryDate = input.DeliveryDate, CreatedAtUtc = now.ToUniversalTime()
        };
        task._lines.AddRange(input.Lines.Select(x => new ProductionTaskLine(x))); return task;
    }
}
