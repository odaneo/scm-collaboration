using Procurement.Domain;

namespace Procurement.Application;

public static class Roles
{
    public const string Buyer = "Buyer";
    public const string Quality = "Quality";
    public const string Factory = "Factory";
}
public sealed record Actor(string SubjectId, string Role, Guid? FactoryId);
public enum FailureKind { InvalidInput, Forbidden, NotFound, Conflict }
public sealed class UseCaseFailure(FailureKind kind, string message) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
}
public sealed record CreateDraftLine(Guid SkuId, int Quantity);
public sealed record CreateDraftRequest(Guid FactoryId, DateOnly DeliveryDate, IReadOnlyList<CreateDraftLine>? Lines);
public sealed record UpdateDraftRequest(int ExpectedRevision, Guid FactoryId, DateOnly DeliveryDate,
    IReadOnlyList<CreateDraftLine>? Lines, string? Reason = null);
public sealed record RevisionRequest(int ExpectedRevision);
public sealed record ReasonRequest(int ExpectedRevision, string? Reason);
public sealed record FactoryOption(Guid Id, string Name);
public sealed record SkuOption(Guid Id, string Style, string Color, string Size);
public sealed record CreatedOrder(Guid OrderId, string Status, int Revision, DateTimeOffset CreatedAt);
public sealed record OrderSummary(Guid Id, string FactoryName, DateOnly DeliveryDate, string Status,
    int Revision, DateTimeOffset CreatedAt);
public sealed record OrderLineDetail(Guid Id, Guid SkuId, string Style, string Color, string Size, int Quantity);
public sealed record OrderDetail(Guid Id, Guid FactoryId, string FactoryName, DateOnly DeliveryDate,
    string Status, int Revision, string CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<OrderLineDetail> Lines,
    int LastSubmittedVersion = 0, int? AcceptedOrderVersion = null);
public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, int Total, int Page, int PageSize);
public sealed record OrderOperationResult(Guid OrderId, int? OrderVersion, string OrderStatus, int Revision,
    string? VersionStatus, Guid? DecisionId);
public sealed record VersionSummary(Guid OrderId, int Version, string FactoryName, DateOnly DeliveryDate,
    string Status, DateTimeOffset SubmittedAt, int? ExpectedRevision);
public sealed record VersionPage(IReadOnlyList<VersionSummary> Items, int Total, int Page, int PageSize);
public sealed record VersionDetail(Guid OrderId, int Version, Guid FactoryId, string FactoryName,
    DateOnly DeliveryDate, int SubmittedRevision, string SubmittedBy, DateTimeOffset SubmittedAt,
    string Status, Guid? DecisionId, string? ResolvedBy, DateTimeOffset? ResolvedAt, int? ResolvedRevision,
    string? Reason, int? ExpectedRevision, IReadOnlyList<OrderLineDetail> Lines);
public sealed record AuditDetail(Guid Id, string Action, string SubjectId, DateTimeOffset OccurredAt,
    int ResultRevision, int? OrderVersion, string? Reason, string? Changes);
public sealed record AuditPage(IReadOnlyList<AuditDetail> Items, int Total, int Page, int PageSize);
public sealed record OrderChange(Actor Actor, Guid OrderId, int? Version, int ExpectedRevision,
    string Operation, string Key, string Hash, DateTimeOffset OccurredAt);
public sealed record OrderMutation(SubmittedOrderVersion? NewVersion = null, string? Reason = null, string? Changes = null);

// 这是当前用例的持久化端口，不是通用 CRUD 仓储。
public interface IProcurementStore
{
    Task<IReadOnlyList<FactoryOption>> GetFactories(CancellationToken ct);
    Task<IReadOnlyList<SkuOption>> GetSkus(CancellationToken ct);
    Task<CreatedOrder?> FindRequest(string subject, string key, string hash, CancellationToken ct);
    Task<CreatedOrder> SaveDraft(PurchaseOrder order, string key, string hash, CancellationToken ct);
    Task<OrderPage> ListOrders(int page, int pageSize, CancellationToken ct);
    Task<OrderDetail?> GetOrder(Guid id, CancellationToken ct);
    Task<OrderOperationResult> ChangeOrder(OrderChange change,
        Func<PurchaseOrder, SubmittedOrderVersion?, CancellationToken, Task<OrderMutation>> apply, CancellationToken ct);
    Task<VersionPage> GetVersions(Guid? factoryId, Guid? orderId, string? status, int page, int pageSize, CancellationToken ct);
    Task<VersionDetail?> GetVersion(Guid? factoryId, Guid orderId, int version, CancellationToken ct);
    Task<AuditPage> GetAudit(Guid id, int page, int pageSize, CancellationToken ct);
}
