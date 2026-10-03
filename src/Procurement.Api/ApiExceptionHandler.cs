using Microsoft.AspNetCore.Diagnostics;
using Procurement.Application;
using Procurement.Domain;

namespace Procurement.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            DomainRuleViolation => 400,
            UseCaseFailure { Kind: FailureKind.InvalidInput } => 400,
            UseCaseFailure { Kind: FailureKind.Forbidden } => 403,
            UseCaseFailure { Kind: FailureKind.Conflict } => 409,
            _ => 500
        };
        if (status == 500) logger.LogError(exception, "请求处理失败，事务不会保存部分业务结果。");
        context.Response.StatusCode = status;
        await problems.WriteAsync(new()
        {
            HttpContext = context,
            ProblemDetails = new()
            {
                Status = status, Title = status == 500 ? "服务暂时无法完成请求" : "请求无法完成",
                Detail = status == 500 ? "请保留幂等键后重试，并提供 traceId 以便排查。" : exception.Message
            }
        });
        return true;
    }
}
