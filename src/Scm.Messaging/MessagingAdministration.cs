using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using Scm.IntegrationContracts;

namespace Scm.Messaging;

public static class MessagingAdministration
{
    public static async Task Initialize(IConfiguration config, CancellationToken ct = default)
    {
        var vhost = config["Messaging:VirtualHost"] ?? "scm";
        var admin = config["MessagingAdmin:Username"] ?? throw new InvalidOperationException("缺少消息管理员用户名。");
        var password = config["MessagingAdmin:Password"] ?? throw new InvalidOperationException("缺少消息管理员密码。");
        using var http = new HttpClient { BaseAddress = new(config["MessagingAdmin:Url"] ?? "http://127.0.0.1:15679/api/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(admin + ":" + password)));
        var path = Uri.EscapeDataString(vhost);
        await Put(http, "vhosts/" + path, new { }, ct);
        await Put(http, $"permissions/{path}/{Uri.EscapeDataString(admin)}", new { configure = ".*", write = ".*", read = ".*" }, ct);
        foreach (var service in new[] { "Procurement", "Production" })
        {
            var user = config[$"MessagingAdmin:{service}Username"] ?? "scm_" + service.ToLowerInvariant();
            var secret = config[$"MessagingAdmin:{service}Password"] ?? throw new InvalidOperationException("缺少服务消息密码。");
            await Put(http, "users/" + Uri.EscapeDataString(user), new { password = secret, tags = "" }, ct);
            var exchange = service == "Procurement" ? "scm.procurement.events" : "scm.production.events";
            var queue = service == "Procurement" ? "scm.procurement.receipts" : "scm.production.accepted-orders";
            await Put(http, $"permissions/{path}/{Uri.EscapeDataString(user)}", new
            { configure = "^$", write = "^" + exchange.Replace(".", "\\.") + "$", read = "^" + queue.Replace(".", "\\.") + "$" }, ct);
        }
        var options = new MessagingOptions(); config.GetSection("Messaging").Bind(options);
        options.Username = admin; options.Password = password;
        await using var connection = await options.Factory().CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync("scm.procurement.events", ExchangeType.Direct, true, false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync("scm.production.events", ExchangeType.Direct, true, false, cancellationToken: ct);
        await channel.QueueDeclareAsync("scm.production.accepted-orders", true, false, false, cancellationToken: ct);
        await channel.QueueDeclareAsync("scm.procurement.receipts", true, false, false, cancellationToken: ct);
        await channel.QueueBindAsync("scm.production.accepted-orders", "scm.procurement.events", nameof(PurchaseOrderAcceptedV1), cancellationToken: ct);
        foreach (var type in new[] { nameof(ProductionTaskCreatedV1), nameof(ProductionTaskCreationFailedV1) })
            await channel.QueueBindAsync("scm.procurement.receipts", "scm.production.events", type, cancellationToken: ct);
    }
    private static async Task Put(HttpClient http, string path, object data, CancellationToken ct)
    { using var response = await http.PutAsJsonAsync(path, data, ct); response.EnsureSuccessStatusCode(); }

    public static async Task InspectOrRetry<TDb>(TDb db, string consumer, Guid? id, MessageProcessor<TDb> processor, CancellationToken ct = default) where TDb : DbContext
    {
        if (id is { } target)
        {
            // 只恢复原记录，不编辑消息内容、不改变编号、不伪造业务成功。
            var count = await db.Set<InboxMessage>().Where(x => x.ConsumerName == consumer && x.MessageId == target && x.Status != "Processed")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Pending").SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow), ct);
            Console.WriteLine($"已恢复待处理记录：{count}");
            if (count == 0 && !await db.Set<InboxMessage>().AnyAsync(x => x.ConsumerName == consumer && x.MessageId == target, ct))
            {
                var originals = await db.Set<MessageFailure>().AsNoTracking()
                    .Where(x => x.ConsumerName == consumer && x.MessageId == target).Select(x => x.EnvelopeJson).ToListAsync(ct);
                if (originals.Count != 1) throw new InvalidOperationException("找不到唯一的隔离消息原文；请先检查失败记录。");
                await processor.Process(originals[0], ct);
                Console.WriteLine("已重新处理隔离消息原文；仍不合法的契约会继续保留为失败，绝不伪造成功。");
            }
        }
        var records = await db.Set<InboxMessage>().AsNoTracking().Where(x => x.ConsumerName == consumer && x.Status != "Processed")
            .OrderBy(x => x.NextAttemptAt).Take(50).Select(x => new { x.MessageId, x.Status, x.Attempts, x.LastError, x.NextAttemptAt }).ToListAsync(ct);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(records, ContractJson.Options));
        var failures = await db.Set<MessageFailure>().AsNoTracking().Where(x => x.ConsumerName == consumer)
            .OrderByDescending(x => x.OccurredAt).Take(20).Select(x => new { x.MessageId, x.ErrorCode, x.OccurredAt }).ToListAsync(ct);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(failures, ContractJson.Options));
    }
}
