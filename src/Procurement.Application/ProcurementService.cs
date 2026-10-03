using System.Security.Cryptography;
using System.Text.Json;
using Procurement.Domain;

namespace Procurement.Application;

public sealed class ProcurementService(IProcurementStore store, TimeProvider clock)
{
    public Task<IReadOnlyList<FactoryOption>> Factories(Actor actor, CancellationToken ct)
    {
        RequireBuyer(actor);
        return store.GetFactories(ct);
    }
    public Task<IReadOnlyList<SkuOption>> Skus(Actor actor, CancellationToken ct)
    {
        RequireBuyer(actor);
        return store.GetSkus(ct);
    }
    public async Task<CreatedOrder> CreateDraft(Actor actor, CreateDraftRequest request, string? key, CancellationToken ct)
    {
        RequireBuyer(actor);
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new UseCaseFailure(FailureKind.InvalidInput, "需要 1–128 字符的 Idempotency-Key。");
        if (request.FactoryId == Guid.Empty || request.DeliveryDate == default ||
            request.Lines is null || request.Lines.Count is < 1 or > 100 || request.Lines.Any(x => x is null))
            throw new UseCaseFailure(FailureKind.InvalidInput, "请指定工厂、交期和 1–100 个明细。");

        var normalized = new CreateDraftRequest(request.FactoryId, request.DeliveryDate,
            request.Lines.OrderBy(x => x.SkuId).ThenBy(x => x.Quantity).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
        var previous = await store.FindRequest(actor.SubjectId, key, hash, ct);
        if (previous is not null) return previous;

        if (!(await store.GetFactories(ct)).Any(x => x.Id == request.FactoryId))
            throw new UseCaseFailure(FailureKind.InvalidInput, "工厂不存在或不可用。");
        var skus = (await store.GetSkus(ct)).ToDictionary(x => x.Id);
        var lines = request.Lines.Select(line =>
        {
            if (!skus.TryGetValue(line.SkuId, out var sku))
                throw new UseCaseFailure(FailureKind.InvalidInput, "商品不存在或不可用。");
            return new DraftLine(sku.Id, new(sku.Style, sku.Color, sku.Size), PieceQuantity.From(line.Quantity));
        });
        var order = PurchaseOrder.CreateDraft(request.FactoryId, request.DeliveryDate,
            actor.SubjectId, clock.GetUtcNow(), lines);
        return await store.SaveDraft(order, key, hash, ct);
    }
    public Task<OrderPage> List(Actor actor, int page, int pageSize, CancellationToken ct)
    {
        RequireBuyer(actor);
        if (page < 1 || page > 100000 || pageSize is < 1 or > 100)
            throw new UseCaseFailure(FailureKind.InvalidInput, "分页参数超出范围。");
        return store.ListOrders(page, pageSize, ct);
    }
    public Task<OrderDetail?> Detail(Actor actor, Guid id, CancellationToken ct) =>
        actor.Role == Roles.Buyer ? store.GetOrder(id, ct) : Task.FromResult<OrderDetail?>(null);

    private static void RequireBuyer(Actor actor)
    {
        if (actor.Role != Roles.Buyer)
            throw new UseCaseFailure(FailureKind.Forbidden, "只有品牌采购人员可以访问订单草稿。");
    }
}
