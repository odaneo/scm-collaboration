using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Production.Api;
using Production.Persistence;
using Scm.Messaging;

namespace Procurement.Tests;

public sealed class ProductionFactory(string connection, string key, FailAfterSave failure) : WebApplicationFactory<ProductionProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); builder.UseSetting("ConnectionStrings:Production", connection); builder.UseSetting("Auth:SigningKey", key);
        builder.UseSetting("Messaging:Enabled", "false"); builder.UseSetting("Messaging:VirtualHost", "scm_1c_tests");
        builder.UseSetting("Messaging:Port", Environment.GetEnvironmentVariable("SCM_TEST_MQ_PORT") ?? "56729");
        builder.UseSetting("Messaging:Username", "scm_production_test");
        builder.UseSetting("Messaging:Password", Environment.GetEnvironmentVariable("SCM_TEST_MQ_PRODUCTION_PASSWORD"));
        builder.UseSetting("Messaging:RetryMilliseconds", "100"); builder.UseSetting("Messaging:PollMilliseconds", "50");
        builder.ConfigureServices(services => services.AddDbContext<ProductionDbContext>(options => options.AddInterceptors(failure)));
    }
    public static async Task<ProductionFactory> Create(string key, FailAfterSave failure)
    {
        var connection = Environment.GetEnvironmentVariable("SCM_PRODUCTION_TEST_CONNECTION") ?? throw new InvalidOperationException("缺少真实生产测试库。");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database != "scm_production_test") throw new InvalidOperationException("只允许专用 scm_production_test。");
        await using var db = new ProductionDbContext(new DbContextOptionsBuilder<ProductionDbContext>().UseNpgsql(connection).Options);
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;"); await db.Database.MigrateAsync();
        var admin = Environment.GetEnvironmentVariable("SCM_TEST_MQ_ADMIN_PASSWORD") ?? throw new InvalidOperationException("真实 RabbitMQ 测试必需。");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:VirtualHost"] = "scm_1c_tests", ["MessagingAdmin:Username"] = "scm_mq_admin", ["MessagingAdmin:Password"] = admin,
            ["Messaging:Port"] = Environment.GetEnvironmentVariable("SCM_TEST_MQ_PORT") ?? "56729",
            ["MessagingAdmin:Url"] = Environment.GetEnvironmentVariable("SCM_TEST_MQ_ADMIN_URL") ?? "http://127.0.0.1:15679/api/",
            ["MessagingAdmin:ProcurementUsername"] = "scm_procurement_test", ["MessagingAdmin:ProductionUsername"] = "scm_production_test",
            ["MessagingAdmin:ProcurementPassword"] = Environment.GetEnvironmentVariable("SCM_TEST_MQ_PROCUREMENT_PASSWORD"),
            ["MessagingAdmin:ProductionPassword"] = Environment.GetEnvironmentVariable("SCM_TEST_MQ_PRODUCTION_PASSWORD")
        }).Build();
        // 独立测试 vhost 的拓扑与凭证，不清空/消费演示 vhost。
        await MessagingAdministration.Initialize(config);
        var options = new MessagingOptions { VirtualHost = "scm_1c_tests", Username = "scm_mq_admin", Password = admin,
            Port = int.Parse(Environment.GetEnvironmentVariable("SCM_TEST_MQ_PORT") ?? "56729") };
        await using var connectionToBroker = await options.Factory().CreateConnectionAsync();
        await using var channel = await connectionToBroker.CreateChannelAsync();
        await channel.QueuePurgeAsync("scm.production.accepted-orders"); await channel.QueuePurgeAsync("scm.procurement.receipts");
        return new(connection, key, failure);
    }
}
