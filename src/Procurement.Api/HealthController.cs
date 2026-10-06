using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Procurement.Persistence;
using Scm.Messaging;

namespace Procurement.Api;

[ApiController, Route("health")]
public sealed class HealthController(ProcurementDbContext db, MessagingState messaging) : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "live" });
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) => await db.Database.CanConnectAsync(ct)
        ? Ok(new { status = "ready", messaging = messaging.Connected ? "connected" : "recovering" }) : Problem(statusCode: 503, detail: "采购数据库暂时不可用。");
}
