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

    public Task<OrderOperationResult> UpdateDraft(Actor actor, Guid id, UpdateDraftRequest request, string? key, CancellationToken ct)
    {
        RequireBuyer(actor);
        var draft = NormalizeDraft(new(request.FactoryId, request.DeliveryDate, request.Lines));
        var normalized = request with { Lines = draft.Lines, Reason = NormalizeReason(request.Reason) };
        return Change(actor, id, null, request.ExpectedRevision, "UpdateDraft", normalized, key, async (order, _, token) =>
        {
            var before = DraftState(order);
            var (_, lines) = await PrepareDraft(draft, token);
            var changed = order.UpdateDraft(draft.FactoryId, draft.DeliveryDate, lines);
            return new(Reason: normalized.Reason, Changes: changed
                ? JsonSerializer.Serialize(new { Before = before, After = DraftState(order) }) : null);
        }, ct);
    }
    public Task<OrderOperationResult> Submit(Actor actor, Guid id, RevisionRequest request, string? key, CancellationToken ct)
    {
        RequireBuyer(actor);
        return Change(actor, id, null, request.ExpectedRevision, "SubmitOrder", request, key, async (order, _, token) =>
        {
            var factory = (await store.GetFactories(token)).SingleOrDefault(x => x.Id == order.FactoryId)
                ?? throw new UseCaseFailure(FailureKind.InvalidInput, "工厂不存在或不可用。");
            var active = (await store.GetSkus(token)).Select(x => x.Id).ToHashSet();
            if (order.Lines.Any(x => !active.Contains(x.SkuId)))
                throw new UseCaseFailure(FailureKind.InvalidInput, "订单包含不可用商品，请先修改草稿。");
            var at = clock.GetUtcNow();
            return new(order.Submit(factory.Name, actor.SubjectId, at, ShanghaiDate(at)));
        }, ct);
    }
    public Task<OrderOperationResult> Withdraw(Actor actor, Guid id, int version, ReasonRequest request, string? key, CancellationToken ct)
    {
        RequireBuyer(actor);
        var normalized = request with { Reason = NormalizeReason(request.Reason) };
        return Change(actor, id, version, request.ExpectedRevision, "WithdrawSubmission", normalized, key, (order, submitted, _) =>
        {
            order.WithdrawSubmission(submitted!, actor.SubjectId, clock.GetUtcNow(), normalized.Reason);
            return Task.FromResult(new OrderMutation(Reason: normalized.Reason));
        }, ct);
    }
    public Task<OrderOperationResult> Accept(Actor actor, Guid id, int version, RevisionRequest request, string? key, CancellationToken ct)
    {
        var factoryId = RequireFactory(actor);
        return Change(actor, id, version, request.ExpectedRevision, "AcceptVersion", request, key, (order, submitted, _) =>
        {
            order.AcceptVersion(submitted!, factoryId, actor.SubjectId, clock.GetUtcNow());
            return Task.FromResult(new OrderMutation());
        }, ct);
    }
    public Task<OrderOperationResult> Reject(Actor actor, Guid id, int version, ReasonRequest request, string? key, CancellationToken ct)
    {
        var factoryId = RequireFactory(actor);
        var normalized = request with { Reason = NormalizeReason(request.Reason) };
        return Change(actor, id, version, request.ExpectedRevision, "RejectVersion", normalized, key, (order, submitted, _) =>
        {
            order.RejectVersion(submitted!, factoryId, actor.SubjectId, clock.GetUtcNow(), normalized.Reason);
            return Task.FromResult(new OrderMutation(Reason: normalized.Reason));
        }, ct);
    }
    public async Task<VersionPage> Versions(Actor actor, Guid id, int page, int pageSize, CancellationToken ct)
    {
        RequireBuyer(actor); ValidatePage(page, pageSize);
        if (await store.GetOrder(id, ct) is null) throw MissingOrder();
        return await store.GetVersions(null, id, null, page, pageSize, ct);
    }
    public Task<VersionPage> FactoryVersions(Actor actor, string? status, int page, int pageSize, CancellationToken ct)
    {
        var factory = RequireFactory(actor); ValidatePage(page, pageSize);
        if (status is not (null or "Pending" or "Accepted" or "Rejected" or "Withdrawn"))
            throw new UseCaseFailure(FailureKind.InvalidInput, "版本状态筛选无效。");
        return store.GetVersions(factory, null, status, page, pageSize, ct);
    }
    public Task<VersionDetail?> Version(Actor actor, Guid id, int version, bool factoryView, CancellationToken ct)
    {
        Guid? factory = null;
        if (factoryView) factory = RequireFactory(actor); else RequireBuyer(actor);
        return store.GetVersion(factory, id, version, ct);
    }
    public async Task<AuditPage> Audit(Actor actor, Guid id, int page, int pageSize, CancellationToken ct)
    {
        RequireBuyer(actor); ValidatePage(page, pageSize);
        if (await store.GetOrder(id, ct) is null) throw MissingOrder();
        return await store.GetAudit(id, page, pageSize, ct);
    }
    public static DateOnly ShanghaiDate(DateTimeOffset at) => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).DateTime);

    private Task<OrderOperationResult> Change(Actor actor, Guid id, int? version, int revision, string operation,
        object payload, string? key, Func<PurchaseOrder, SubmittedOrderVersion?, CancellationToken, Task<OrderMutation>> apply, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || revision < 1 || version is <= 0)
            throw new UseCaseFailure(FailureKind.InvalidInput, "需要合法幂等键、正整数 ExpectedRevision 和版本号。");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { OrderId = id, Version = version, Payload = payload })));
        return store.ChangeOrder(new(actor, id, version, revision, operation, key, hash, clock.GetUtcNow()), apply, ct);
    }
    private static CreateDraftRequest NormalizeDraft(CreateDraftRequest request)
    {
        if (request.FactoryId == Guid.Empty || request.DeliveryDate == default || request.Lines is null ||
            request.Lines.Count is < 1 or > 100 || request.Lines.Any(x => x is null))
            throw new UseCaseFailure(FailureKind.InvalidInput, "请指定工厂、交期和 1–100 个明细。");
        return request with { Lines = request.Lines.OrderBy(x => x.SkuId).ThenBy(x => x.Quantity).ToArray() };
    }
    private async Task<(FactoryOption Factory, DraftLine[] Lines)> PrepareDraft(CreateDraftRequest request, CancellationToken ct)
    {
        var factory = (await store.GetFactories(ct)).SingleOrDefault(x => x.Id == request.FactoryId)
            ?? throw new UseCaseFailure(FailureKind.InvalidInput, "工厂不存在或不可用。");
        var skus = (await store.GetSkus(ct)).ToDictionary(x => x.Id);
        var lines = request.Lines!.Select(line =>
        {
            if (!skus.TryGetValue(line.SkuId, out var sku)) throw new UseCaseFailure(FailureKind.InvalidInput, "商品不存在或不可用。");
            return new DraftLine(sku.Id, new(sku.Style, sku.Color, sku.Size), PieceQuantity.From(line.Quantity));
        }).ToArray();
        return (factory, lines);
    }
    private static object DraftState(PurchaseOrder order) => new
    {
        order.FactoryId, order.DeliveryDate,
        Lines = order.Lines.OrderBy(x => x.SkuId).Select(x => new { x.Id, x.SkuId, Quantity = x.Quantity.Value }).ToArray()
    };
    private static string? NormalizeReason(string? reason)
    {
        var text = reason?.Trim();
        if (text?.Length > 500) throw new UseCaseFailure(FailureKind.InvalidInput, "原因不能超过 500 字符。");
        return string.IsNullOrEmpty(text) ? null : text;
    }
    private static Guid RequireFactory(Actor actor) => actor.Role == Roles.Factory && actor.FactoryId is { } id && id != Guid.Empty
        ? id : throw new UseCaseFailure(FailureKind.Forbidden, "只有具备工厂归属的工厂人员可以执行此操作。");
    private static void ValidatePage(int page, int pageSize)
    {
        if (page is < 1 or > 100000 || pageSize is < 1 or > 100)
            throw new UseCaseFailure(FailureKind.InvalidInput, "分页参数超出范围。");
    }
    private static UseCaseFailure MissingOrder() => new(FailureKind.NotFound, "订单不存在或不可见。");

    private static void RequireBuyer(Actor actor)
    {
        if (actor.Role != Roles.Buyer)
            throw new UseCaseFailure(FailureKind.Forbidden, "只有品牌采购人员可以访问订单草稿。");
    }
}
