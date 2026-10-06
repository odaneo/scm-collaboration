using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Procurement.Api;
using Procurement.Application;
using Procurement.Persistence;
using Scm.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
var signingKey = Convert.FromBase64String(builder.Configuration["Auth:SigningKey"]
    ?? throw new InvalidOperationException("缺少 Auth:SigningKey，请运行本地启动脚本。"));
if (signingKey.Length < 32) throw new InvalidOperationException("JWT 密钥至少需要 32 字节。");
builder.Services.AddDbContext<ProcurementDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Procurement")
    ?? throw new InvalidOperationException("缺少采购数据库连接配置。")));
builder.Services.AddScoped<IProcurementStore, ProcurementStore>();
builder.Services.AddScoped<ProcurementService>();
builder.Services.AddScmMessaging<ProcurementDbContext, ProcurementMessageHandler>(builder.Configuration, "Procurement");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PasswordHasher<DemoUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            var subject = context.Principal?.FindFirst("sub")?.Value;
            var role = context.Principal?.FindFirst("role")?.Value;
            if (string.IsNullOrWhiteSpace(subject) || role is not (Roles.Buyer or Roles.Quality or Roles.Factory) ||
                (role == Roles.Factory && (!Guid.TryParse(context.Principal?.FindFirst("factory_id")?.Value, out var factoryId) || factoryId == Guid.Empty)))
                context.Fail("身份声明不完整。");
            return Task.CompletedTask;
        }
    };
    options.TokenValidationParameters = new()
    {
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(signingKey),
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Auth:Issuer"],
        ValidateAudience = true, ValidAudience = builder.Configuration["Auth:Audience"],
        ValidateLifetime = true, ClockSkew = TimeSpan.Zero, RoleClaimType = "role", NameClaimType = "name"
    };
});
builder.Services.AddAuthorization();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(new ProblemDetails
    {
        Status = 400, Title = "请检查输入格式",
        Detail = context.ActionDescriptor.RouteValues["controller"] == "DemoAuth"
            ? "请输入完整的账号和密码。"
            : "请检查请求字段：工厂、商品、YYYY-MM-DD 交期、整数件数及 ExpectedRevision；拒绝或撤回需要原因。",
        Extensions = { ["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier }
    });
});
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new()
    { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);

var app = builder.Build();
if (args.Contains("--initialize-messaging") || args.Contains("--backfill-production") || args.Contains("--inspect-messages") || args.Contains("--retry-message"))
{
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("演示维护命令只允许 Development。");
    using var maintenanceScope = app.Services.CreateScope();
    var maintenanceDb = maintenanceScope.ServiceProvider.GetRequiredService<ProcurementDbContext>();
    if (args.Contains("--initialize-messaging")) await MessagingAdministration.Initialize(app.Configuration);
    else if (args.Contains("--backfill-production")) await ProductionTaskBackfill.Run(maintenanceDb, args.Contains("--apply"));
    else
    {
        Guid? id = args.Contains("--retry-message") ? Guid.Parse(args[Array.IndexOf(args, "--retry-message") + 1]) : null;
        await MessagingAdministration.InspectOrRetry(maintenanceDb, maintenanceScope.ServiceProvider.GetRequiredService<MessagingOptions>().ConsumerName, id,
            maintenanceScope.ServiceProvider.GetRequiredService<MessageProcessor<ProcurementDbContext>>());
    }
    return;
}
if (args.Contains("--initialize"))
{
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("演示初始化只允许 Development 环境。");
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ProcurementDbContext>();
    await db.Database.MigrateAsync();
    await DemoSeed.Run(db, scope.ServiceProvider.GetRequiredService<PasswordHasher<DemoUser>>(),
        app.Configuration["DemoAuth:Password"] ?? throw new InvalidOperationException("缺少本地演示密码。"));
    app.Logger.LogInformation("采购数据库迁移和演示资料初始化完成。");
    return;
}
app.Use(async (context, next) =>
{
    var start = Stopwatch.GetTimestamp();
    using var scope = app.Logger.BeginScope(new Dictionary<string, object?>
    { ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier });
    try { await next(context); }
    finally
    {
        app.Logger.LogInformation("HTTP {Method} {Path} {StatusCode} {ElapsedMs}",
            context.Request.Method, context.Request.Path, context.Response.StatusCode,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Run();

public partial class Program;
