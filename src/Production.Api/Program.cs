using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Production.Application;
using Production.Persistence;
using Scm.Messaging;

namespace Production.Api;

public partial class ProductionProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(x => x.IncludeScopes = true);
        var key = Convert.FromBase64String(builder.Configuration["Auth:SigningKey"] ?? throw new InvalidOperationException("缺少 JWT 验证密钥。"));
        if (key.Length < 32) throw new InvalidOperationException("JWT 密钥至少需要 32 字节。");
        builder.Services.AddDbContext<ProductionDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Production")
            ?? throw new InvalidOperationException("缺少生产数据库连接。")));
        builder.Services.AddScoped<IProductionStore, ProductionStore>(); builder.Services.AddScoped<ProductionService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScmMessaging<ProductionDbContext, ProductionMessageHandler>(builder.Configuration, "Production");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true, ValidIssuer = builder.Configuration["Auth:Issuer"],
                ValidateAudience = true, ValidAudience = builder.Configuration["Auth:Audience"],
                ValidateLifetime = true, ClockSkew = TimeSpan.Zero, RoleClaimType = "role", NameClaimType = "name"
            };
            options.Events = new JwtBearerEvents { OnTokenValidated = context =>
            {
                var role = context.Principal?.FindFirst("role")?.Value;
                if (string.IsNullOrWhiteSpace(context.Principal?.FindFirst("sub")?.Value) || role is not ("Buyer" or "Factory" or "Quality") ||
                    (role == "Factory" && (!Guid.TryParse(context.Principal?.FindFirst("factory_id")?.Value, out var factory) || factory == Guid.Empty)))
                    context.Fail("身份声明不完整。");
                return Task.CompletedTask;
            } };
        });
        builder.Services.AddAuthorization(); builder.Services.AddControllers(); builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
        builder.Services.AddExceptionHandler<ProductionExceptionHandler>();
        var app = builder.Build();
        if (args.Contains("--initialize") || args.Contains("--inspect-messages") || args.Contains("--retry-message"))
        {
            if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("演示维护命令只允许 Development。");
            using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
            if (args.Contains("--initialize")) { await db.Database.MigrateAsync(); app.Logger.LogInformation("生产数据库迁移完成。"); }
            else
            {
                Guid? id = args.Contains("--retry-message") ? Guid.Parse(args[Array.IndexOf(args, "--retry-message") + 1]) : null;
                await MessagingAdministration.InspectOrRetry(db, scope.ServiceProvider.GetRequiredService<MessagingOptions>().ConsumerName, id,
                    scope.ServiceProvider.GetRequiredService<MessageProcessor<ProductionDbContext>>());
            }
            return;
        }
        app.Use(async (context, next) =>
        {
            var start = Stopwatch.GetTimestamp();
            using var scope = app.Logger.BeginScope(new Dictionary<string, object?> { ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier });
            try { await next(context); } finally { app.Logger.LogInformation("HTTP {Method} {Path} {StatusCode} {ElapsedMs}", context.Request.Method,
                context.Request.Path, context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
        });
        app.UseExceptionHandler(); app.UseStatusCodePages(); app.UseAuthentication(); app.UseAuthorization();
        app.MapControllers(); if (app.Environment.IsDevelopment()) app.MapOpenApi(); await app.RunAsync();
    }
}
