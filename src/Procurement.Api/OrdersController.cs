using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Procurement.Application;

namespace Procurement.Api;

[ApiController, Authorize, Route("api")]
public sealed class OrdersController(ProcurementService service) : ControllerBase
{
    private Actor CurrentActor => new(User.FindFirst("sub")!.Value, User.FindFirst("role")?.Value ?? "",
        Guid.TryParse(User.FindFirst("factory_id")?.Value, out var id) ? id : null);

    [HttpGet("factories")]
    public async Task<IActionResult> Factories(CancellationToken ct) => Ok(await service.Factories(CurrentActor, ct));
    [HttpGet("skus")]
    public async Task<IActionResult> Skus(CancellationToken ct) => Ok(await service.Skus(CurrentActor, ct));

    [HttpPost("purchase-orders")]
    public async Task<IActionResult> Create(CreateDraftRequest request, CancellationToken ct)
    {
        var result = await service.CreateDraft(CurrentActor, request, Request.Headers["Idempotency-Key"].ToString(), ct);
        return CreatedAtAction(nameof(Detail), new { id = result.OrderId }, result);
    }
    [HttpGet("purchase-orders")]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(await service.List(CurrentActor, page, pageSize, ct));

    [HttpGet("purchase-orders/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var order = await service.Detail(CurrentActor, id, ct);
        return order is null ? NotFound() : Ok(order);
    }
    [HttpPut("purchase-orders/{id:guid}/draft")]
    public async Task<IActionResult> UpdateDraft(Guid id, UpdateDraftRequest request, CancellationToken ct) =>
        Ok(await service.UpdateDraft(CurrentActor, id, request, Key, ct));
    [HttpPost("purchase-orders/{id:guid}/submissions")]
    public async Task<IActionResult> Submit(Guid id, RevisionRequest request, CancellationToken ct) =>
        Ok(await service.Submit(CurrentActor, id, request, Key, ct));
    [HttpPost("purchase-orders/{id:guid}/versions/{version:int}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, int version, ReasonRequest request, CancellationToken ct) =>
        Ok(await service.Withdraw(CurrentActor, id, version, request, Key, ct));
    [HttpPost("purchase-orders/{id:guid}/versions/{version:int}/accept")]
    public async Task<IActionResult> Accept(Guid id, int version, RevisionRequest request, CancellationToken ct) =>
        Ok(await service.Accept(CurrentActor, id, version, request, Key, ct));
    [HttpPost("purchase-orders/{id:guid}/versions/{version:int}/reject")]
    public async Task<IActionResult> Reject(Guid id, int version, ReasonRequest request, CancellationToken ct) =>
        Ok(await service.Reject(CurrentActor, id, version, request, Key, ct));
    [HttpGet("purchase-orders/{id:guid}/versions")]
    public async Task<IActionResult> Versions(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(await service.Versions(CurrentActor, id, page, pageSize, ct));
    [HttpGet("purchase-orders/{id:guid}/versions/{version:int}")]
    public async Task<IActionResult> Version(Guid id, int version, CancellationToken ct) =>
        VersionResponse(await service.Version(CurrentActor, id, version, false, ct));
    [HttpGet("factory/order-versions")]
    public async Task<IActionResult> FactoryVersions(CancellationToken ct, string? status = null, int page = 1, int pageSize = 20) =>
        Ok(await service.FactoryVersions(CurrentActor, status, page, pageSize, ct));
    [HttpGet("factory/orders/{id:guid}/versions/{version:int}")]
    public async Task<IActionResult> FactoryVersion(Guid id, int version, CancellationToken ct) =>
        VersionResponse(await service.Version(CurrentActor, id, version, true, ct));
    [HttpGet("purchase-orders/{id:guid}/audit")]
    public async Task<IActionResult> Audit(Guid id, CancellationToken ct, int page = 1, int pageSize = 20) =>
        Ok(await service.Audit(CurrentActor, id, page, pageSize, ct));
    private string Key => Request.Headers["Idempotency-Key"].ToString();
    private IActionResult VersionResponse(VersionDetail? version) => version is null ? NotFound() : Ok(version);
}
