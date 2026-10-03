using Procurement.Domain;

namespace Procurement.Application;

public static class Roles
{
    public const string Buyer = "Buyer";
    public const string Quality = "Quality";
    public const string Factory = "Factory";
}
public sealed record Actor(string SubjectId, string Role, Guid? FactoryId);
public enum FailureKind { InvalidInput, Forbidden, Conflict }
public sealed class UseCaseFailure(FailureKind kind, string message) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
}
public sealed record CreateDraftLine(Guid SkuId, int Quantity);
public sealed record CreateDraftRequest(Guid FactoryId, DateOnly DeliveryDate, IReadOnlyList<CreateDraftLine>? Lines);
public sealed record FactoryOption(Guid Id, string Name);
public sealed record SkuOption(Guid Id, string Style, string Color, string Size);
public sealed record CreatedOrder(Guid OrderId, string Status, int Revision, DateTimeOffset CreatedAt);
public sealed record OrderSummary(Guid Id, string FactoryName, DateOnly DeliveryDate, string Status,
    int Revision, DateTimeOffset CreatedAt);
public sealed record OrderLineDetail(Guid Id, Guid SkuId, string Style, string Color, string Size, int Quantity);
public sealed record OrderDetail(Guid Id, Guid FactoryId, string FactoryName, DateOnly DeliveryDate,
    string Status, int Revision, string CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<OrderLineDetail> Lines);
public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, int Total, int Page, int PageSize);

// 这是当前用例的持久化端口，不是通用 CRUD 仓储。
public interface IProcurementStore
{
    Task<IReadOnlyList<FactoryOption>> GetFactories(CancellationToken ct);
    Task<IReadOnlyList<SkuOption>> GetSkus(CancellationToken ct);
    Task<CreatedOrder?> FindRequest(string subject, string key, string hash, CancellationToken ct);
    Task<CreatedOrder> SaveDraft(PurchaseOrder order, string key, string hash, CancellationToken ct);
    Task<OrderPage> ListOrders(int page, int pageSize, CancellationToken ct);
    Task<OrderDetail?> GetOrder(Guid id, CancellationToken ct);
}
