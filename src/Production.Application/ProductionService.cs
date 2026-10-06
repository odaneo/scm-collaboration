using Production.Domain;

namespace Production.Application;

public sealed record ProductionActor(string SubjectId, string Role, Guid? FactoryId);
public sealed class ProductionUseCaseFailure(int status, string message) : Exception(message) { public int Status => status; }
public sealed class AcceptedContentConflict : Exception;
public sealed record TaskLineDetail(Guid LineId, Guid SkuId, string Style, string Color, string Size, int ConfirmedQuantity);
public sealed record TaskDetail(Guid Id, Guid OrderId, int AcceptedOrderVersion, Guid FactoryId, string FactoryName,
    DateOnly DeliveryDate, DateTimeOffset CreatedAtUtc, IReadOnlyList<TaskLineDetail> Lines);
public sealed record TaskSummary(Guid Id, Guid OrderId, int AcceptedOrderVersion, string FactoryName, DateOnly DeliveryDate);
public sealed record TaskPage(IReadOnlyList<TaskSummary> Items, int Total, int Page, int PageSize);
public interface IProductionStore
{
    Task<(ProductionTask Task, string Hash)?> FindAcceptance(Guid orderId, Guid decisionId, CancellationToken ct);
    void Add(ProductionTask task, string contentHash);
    Task<TaskPage> List(Guid? factoryId, int page, int pageSize, CancellationToken ct);
    Task<TaskDetail?> Detail(Guid? factoryId, Guid id, CancellationToken ct);
}
public sealed class ProductionService(IProductionStore store, TimeProvider clock)
{
    public async Task<(ProductionTask Task, bool Created)> Initialize(ConfirmedOrder input, string hash, CancellationToken ct)
    {
        var previous = await store.FindAcceptance(input.OrderId, input.DecisionId, ct);
        if (previous is { } existing)
        {
            if (existing.Task.OrderId != input.OrderId || existing.Task.DecisionId != input.DecisionId ||
                existing.Task.AcceptedOrderVersion != input.Version || existing.Task.FactoryId != input.FactoryId || existing.Hash != hash)
                throw new AcceptedContentConflict();
            return (existing.Task, false);
        }
        var task = ProductionTask.CreateFromAcceptedOrder(input, clock.GetUtcNow()); store.Add(task, hash);
        return (task, true);
    }
    public Task<TaskPage> List(ProductionActor actor, int page, int pageSize, CancellationToken ct)
    {
        var factory = FactoryScope(actor);
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100) throw new ProductionUseCaseFailure(400, "分页参数超出范围。");
        return store.List(factory, page, pageSize, ct);
    }
    public Task<TaskDetail?> Detail(ProductionActor actor, Guid id, CancellationToken ct) => store.Detail(FactoryScope(actor), id, ct);
    private static Guid? FactoryScope(ProductionActor actor) => actor.Role switch
    {
        "Buyer" => null,
        "Factory" when actor.FactoryId is { } id && id != Guid.Empty => id,
        _ => throw new ProductionUseCaseFailure(403, "只有品牌采购或所属工厂人员可以查看生产任务。")
    };
}
