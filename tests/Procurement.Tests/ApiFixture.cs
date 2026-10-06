using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Procurement.Api;
using Procurement.Application;
using Procurement.Persistence;
using Procurement.Domain;

namespace Procurement.Tests;

public sealed class FailAfterSave : SaveChangesInterceptor
{
    public int Armed;
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref Armed, 0) == 1) throw new InvalidOperationException("test: failure after SQL save, before commit");
        return ValueTask.FromResult(result);
    }
}

public sealed class ConcurrentSaveBarrier : SaveChangesInterceptor
{
    private Guid orderId;
    private int remaining;
    private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm(Guid id)
    {
        orderId = id; remaining = 2;
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (data.Context!.ChangeTracker.Entries<PurchaseOrder>().Any(x => x.Entity.Id == orderId && x.State == EntityState.Modified))
        {
            if (Interlocked.Decrement(ref remaining) == 0) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        return result;
    }
}

public sealed class ApiFactory(string connection, string key, FailAfterSave failure, string environment = "Development",
    ConcurrentSaveBarrier? barrier = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Procurement", connection);
        builder.UseSetting("Auth:SigningKey", key);
        builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("Messaging:VirtualHost", "scm_1c_tests");
        builder.UseSetting("Messaging:Port", Environment.GetEnvironmentVariable("SCM_TEST_MQ_PORT") ?? "56729");
        builder.UseSetting("Messaging:Username", "scm_procurement_test");
        builder.UseSetting("Messaging:Password", Environment.GetEnvironmentVariable("SCM_TEST_MQ_PROCUREMENT_PASSWORD"));
        builder.UseSetting("Messaging:RetryMilliseconds", "100"); builder.UseSetting("Messaging:PollMilliseconds", "50");
        builder.ConfigureServices(services => services.AddDbContext<ProcurementDbContext>(options =>
        {
            options.AddInterceptors(failure);
            if (barrier is not null) options.AddInterceptors(barrier);
        }));
    }
}

public sealed class ApiFixture : IAsyncLifetime
{
    public string Connection { get; private set; } = "";
    public string Key { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    private string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    public FailAfterSave Failure { get; } = new();
    public ConcurrentSaveBarrier Barrier { get; } = new();
    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Dictionary<string, string> Tokens { get; } = [];
    public ProductionFactory ProductionFactory { get; private set; } = null!;
    public HttpClient ProductionClient { get; private set; } = null!;
    public FailAfterSave ProductionFailure { get; } = new();

    public async Task InitializeAsync()
    {
        Connection = Environment.GetEnvironmentVariable("SCM_TEST_CONNECTION")
            ?? throw new InvalidOperationException("集成测试需要真实 PostgreSQL：请运行 scripts/test.ps1。");
        var parsed = new NpgsqlConnectionStringBuilder(Connection);
        if (parsed.Database is null || !parsed.Database.EndsWith("_test", StringComparison.Ordinal))
            throw new InvalidOperationException("仅允许以 _test 结尾的专用测试数据库；测试会清空其 public schema。");
        await using (var db = new ProcurementDbContext(new DbContextOptionsBuilder<ProcurementDbContext>().UseNpgsql(Connection).Options))
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
            await db.Database.MigrateAsync();
            await DemoSeed.Run(db, new PasswordHasher<DemoUser>(), Password);
        }
        Factory = new(Connection, Key, Failure, barrier: Barrier);
        Client = Factory.CreateClient(new() { AllowAutoRedirect = false });
        foreach (var username in new[] { "buyer", "quality", "factory-a", "factory-b" })
        {
            var response = await Client.PostAsJsonAsync("/api/demo-auth/login", new LoginRequest(username, Password));
            response.EnsureSuccessStatusCode();
            Tokens[username] = (await response.Content.ReadFromJsonAsync<LoginResult>())!.Token;
        }
        ProductionFactory = await ProductionFactory.Create(Key, ProductionFailure);
        ProductionClient = ProductionFactory.CreateClient();
    }
    public static CreateDraftRequest Draft(int quantity = 100, Guid? factory = null, Guid? sku = null) =>
        new(factory ?? DemoSeed.FactoryA, new(2026, 10, 31), [new(sku ?? DemoSeed.SkuM, quantity), new(DemoSeed.SkuL, 50)]);
    public async Task<HttpResponseMessage> Send(HttpMethod method, string url, string? user = "buyer",
        object? body = null, string? key = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (user is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[user]);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await (client ?? Client).SendAsync(request);
    }
    public async Task<T> Db<T>(Func<ProcurementDbContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ProcurementDbContext>());
    }
    public async Task SeedAgain() => await Db(async db =>
    {
        await DemoSeed.Run(db, new PasswordHasher<DemoUser>(), Password);
        return true;
    });
    public Task DisposeAsync()
    {
        Client?.Dispose(); Factory?.Dispose();
        ProductionClient?.Dispose(); ProductionFactory?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<ApiFixture>;
