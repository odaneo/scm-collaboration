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
}
