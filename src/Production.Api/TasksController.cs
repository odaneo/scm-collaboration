using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Production.Application;
using Production.Persistence;
using Scm.Messaging;

namespace Production.Api;

[ApiController, Authorize, Route("api/production-tasks")]
public sealed class TasksController(ProductionService service) : ControllerBase
{
    private ProductionActor Actor => new(User.FindFirst("sub")!.Value, User.FindFirst("role")!.Value,
        Guid.TryParse(User.FindFirst("factory_id")?.Value, out var id) ? id : null);
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, int page = 1, int pageSize = 20) => Ok(await service.List(Actor, page, pageSize, ct));
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    { var task = await service.Detail(Actor, id, ct); return task is null ? NotFound() : Ok(task); }
}
[ApiController, Route("health")]
public sealed class ProductionHealthController(ProductionDbContext db, MessagingState messaging) : ControllerBase
{
    [HttpGet("live")] public IActionResult Live() => Ok(new { status = "live" });
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) => await db.Database.CanConnectAsync(ct)
        ? Ok(new { status = "ready", messaging = messaging.Connected ? "connected" : "recovering" })
        : Problem(statusCode: 503, detail: "生产数据库暂时不可用。");
}
public sealed class ProductionExceptionHandler(IProblemDetailsService problems, ILogger<ProductionExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception is ProductionUseCaseFailure useCase ? useCase.Status : 500;
        if (status == 500) logger.LogError("生产请求处理失败：{ErrorCode}", exception.GetType().Name);
        context.Response.StatusCode = status;
        await problems.WriteAsync(new() { HttpContext = context, ProblemDetails = new()
        { Status = status, Title = "请求无法完成", Detail = status == 500 ? "生产服务暂不可用，请提供 traceId 排查。" : exception.Message } });
        return true;
    }
}
