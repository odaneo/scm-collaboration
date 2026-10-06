using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Scm.IntegrationContracts;

namespace Scm.Messaging;

public sealed class MessagingOptions
{
    public string ServiceName { get; set; } = "Procurement";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 56729;
    public string VirtualHost { get; set; } = "scm";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int RetryMilliseconds { get; set; } = 5000;
    public int PollMilliseconds { get; set; } = 1000;
    public string ConsumerName => ServiceName == "Procurement" ? "Procurement.TaskReceipts" : "Production.AcceptedOrders";
    public string Queue => ServiceName == "Procurement" ? "scm.procurement.receipts" : "scm.production.accepted-orders";
    public string Exchange => ServiceName == "Procurement" ? "scm.procurement.events" : "scm.production.events";
    public ConnectionFactory Factory() => new()
    {
        HostName = Host, Port = Port, VirtualHost = VirtualHost, UserName = Username, Password = Password,
        AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
        ContinuationTimeout = TimeSpan.FromSeconds(5), RequestedHeartbeat = TimeSpan.FromSeconds(5)
    };
}
public sealed class MessagingState
{
    public volatile bool Connected;
    public string? LastError { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
}
public static class MessagingRegistration
{
    public static void AddScmMessaging<TDb, THandler>(this IServiceCollection services, IConfiguration configuration, string name)
        where TDb : DbContext where THandler : class, IIntegrationMessageHandler
    {
        var options = new MessagingOptions(); configuration.GetSection("Messaging").Bind(options); options.ServiceName = name;
        services.AddSingleton(options); services.AddSingleton<MessagingState>();
        services.AddScoped<IIntegrationMessageHandler, THandler>(); services.AddScoped<MessageProcessor<TDb>>();
        if (configuration.GetValue("Messaging:Enabled", true)) services.AddHostedService<MessagingWorker<TDb>>();
    }
}
public sealed class MessagingWorker<TDb>(IServiceScopeFactory scopes, MessagingOptions options, MessagingState state,
    ILogger<MessagingWorker<TDb>> logger) : BackgroundService where TDb : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryPending(stoppingToken);
                await using var connection = await options.Factory().CreateConnectionAsync(options.ServiceName, stoppingToken);
                await using var publish = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken);
                await using var consume = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await consume.BasicQosAsync(0, 1, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(consume);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    // 复制 body，不能在 RabbitMQ 回调结束后持有客户端管理的缓冲区。
                    var raw = Encoding.UTF8.GetString(delivery.Body.ToArray());
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<MessageProcessor<TDb>>().Process(raw, stoppingToken);
                        await consume.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("消费未能持久化，关闭通道等待重投：{ErrorCode}", ex.GetType().Name);
                        await consume.CloseAsync();
                    }
                };
                await consume.BasicConsumeAsync(options.Queue, false, consumer, stoppingToken);
                state.Connected = true; state.LastError = null; state.LastConnectedAt = DateTimeOffset.UtcNow;
                while (!stoppingToken.IsCancellationRequested && connection.IsOpen && publish.IsOpen && consume.IsOpen)
                {
                    await PublishPending(publish, stoppingToken);
                    await RetryPending(stoppingToken);
                    await Task.Delay(options.PollMilliseconds, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                state.LastError = ex.GetType().Name;
                logger.LogWarning("消息协作暂不可用，将恢复连接：{ErrorCode}", state.LastError);
                await RecordConnectionFailure(state.LastError, stoppingToken);
            }
            finally { state.Connected = false; }
            try { await Task.Delay(options.RetryMilliseconds, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
    public async Task PublishPending(IChannel channel, CancellationToken ct)
    {
        // ponytail: 1C 每服务单发布 worker；多实例部署时再加入租约领取，不把此限制用于消费幂等。
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.Set<OutboxMessage>().Where(x => x.SentAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.EnqueuedAt).Take(20).ToListAsync(ct);
        foreach (var row in messages)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(row.EnvelopeJson, ContractJson.Options)!;
                await Send(channel, options.Exchange, envelope, ct);
                // confirm 与本地标记不是原子操作：标记失败后允许重复发送。
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                row.SentAt = DateTimeOffset.UtcNow; row.Attempts++; row.LastError = null;
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                await db.Set<OutboxMessage>().Where(x => x.MessageId == row.MessageId && x.SentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1)
                        .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddMilliseconds(options.RetryMilliseconds))
                        .SetProperty(x => x.LastError, ex.GetType().Name), ct);
                logger.LogWarning("事件发布/确认标记失败 {MessageId} {ErrorCode}", row.MessageId, ex.GetType().Name);
                break;
            }
        }
    }
    public static async Task Send(IChannel channel, string exchange, MessageEnvelope envelope, CancellationToken ct)
    {
        var props = new BasicProperties { Persistent = true, MessageId = envelope.MessageId.ToString(),
            ContentType = "application/json", Type = envelope.EventType, CorrelationId = envelope.CorrelationId };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // 客户端开启 confirm tracking；mandatory return/nack 会使此等待失败。
        await channel.BasicPublishAsync(exchange, envelope.EventType, true, props, Encoding.UTF8.GetBytes(envelope.Serialize()), timeout.Token);
    }
    public async Task RetryPending(CancellationToken ct)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TDb>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.Set<InboxMessage>().AsNoTracking().Where(x => x.ConsumerName == options.ConsumerName &&
            x.Status == "Pending" && x.NextAttemptAt <= now).OrderBy(x => x.NextAttemptAt).Take(20)
            .Select(x => x.EnvelopeJson).ToListAsync(ct);
        foreach (var raw in messages)
        {
            using var attempt = scopes.CreateScope();
            await attempt.ServiceProvider.GetRequiredService<MessageProcessor<TDb>>().Process(raw, ct);
        }
    }
    private async Task RecordConnectionFailure(string error, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TDb>();
            var now = DateTimeOffset.UtcNow;
            await db.Set<OutboxMessage>().Where(x => x.SentAt == null && x.NextAttemptAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1)
                    .SetProperty(x => x.NextAttemptAt, now.AddMilliseconds(options.RetryMilliseconds))
                    .SetProperty(x => x.LastError, error), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { logger.LogWarning("消息恢复记录暂不能保存：{ErrorCode}", ex.GetType().Name); }
    }
}
