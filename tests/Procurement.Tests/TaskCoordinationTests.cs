using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Procurement.Application;
using Procurement.Api;
using Procurement.Persistence;
using Production.Application;
using Production.Persistence;
using RabbitMQ.Client;
using Scm.IntegrationContracts;
using Scm.Messaging;

namespace Procurement.Tests;

[Collection("PostgreSQL")]
public sealed class TaskCoordinationTests(ApiFixture f)
{
    private async Task<MessageEnvelope> Accepted()
    {
        var create = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(), key: Guid.NewGuid().ToString());
        create.EnsureSuccessStatusCode(); var order = (await create.Content.ReadFromJsonAsync<CreatedOrder>())!;
        var submit = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/submissions", body: new RevisionRequest(1), key: Guid.NewGuid().ToString());
        submit.EnsureSuccessStatusCode();
        var accept = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{order.OrderId}/versions/1/accept", "factory-a", new RevisionRequest(2), Guid.NewGuid().ToString());
        accept.EnsureSuccessStatusCode(); var result = (await accept.Content.ReadFromJsonAsync<OrderOperationResult>())!;
        return await f.Db(async db => JsonSerializer.Deserialize<MessageEnvelope>((await db.Set<OutboxMessage>().SingleAsync(x => x.FactId == result.DecisionId)).EnvelopeJson, ContractJson.Options)!);
    }
    private async Task Production(MessageEnvelope envelope)
    {
        using var scope = f.ProductionFactory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessageProcessor<ProductionDbContext>>().Process(envelope.Serialize(), default);
    }
    private async Task Receipt(MessageEnvelope envelope)
    {
        using var scope = f.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessageProcessor<ProcurementDbContext>>().Process(envelope.Serialize(), default);
    }
    private async Task<T> ProductionDb<T>(Func<ProductionDbContext, Task<T>> query)
    { using var scope = f.ProductionFactory.Services.CreateScope(); return await query(scope.ServiceProvider.GetRequiredService<ProductionDbContext>()); }
    private async Task<MessageEnvelope> CreatedReceipt(MessageEnvelope accepted)
    {
        await Production(accepted); var decision = accepted.FactId;
        return await ProductionDb(async db =>
        {
            var taskId = await db.Tasks.Where(x => x.DecisionId == decision).Select(x => x.Id).SingleAsync();
            return JsonSerializer.Deserialize<MessageEnvelope>((await db.Set<OutboxMessage>().SingleAsync(x => x.FactId == taskId)).EnvelopeJson, ContractJson.Options)!;
        });
    }
    private async Task<ProductionTaskStatus> Status(Guid orderId)
    {
        var response = await f.Send(HttpMethod.Get, $"/api/purchase-orders/{orderId}/production-task"); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductionTaskStatus>())!;
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(100); }
        Assert.True(await condition(), "在 20 秒测试等待窗口内未完成预期恢复。");
    }
    private MessagingWorker<TDb> Worker<TDb>(IServiceProvider provider) where TDb : DbContext => new(
        provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<MessagingOptions>(),
        provider.GetRequiredService<MessagingState>(), NullLogger<MessagingWorker<TDb>>.Instance);
    private async Task<IConnection> Broker()
    {
        var options = new MessagingOptions { VirtualHost = "scm_1c_tests", Username = "scm_mq_admin",
            Password = Environment.GetEnvironmentVariable("SCM_TEST_MQ_ADMIN_PASSWORD")!,
            Port = int.Parse(Environment.GetEnvironmentVariable("SCM_TEST_MQ_PORT") ?? "56729") };
        return await options.Factory().CreateConnectionAsync();
    }
    [Fact] public async Task HttpReplayKeepsOneDecisionOutboxAndPendingLink()
    {
        var envelope = await Accepted(); var payload = envelope.Read<PurchaseOrderAcceptedV1>();
        var record = await f.Db(db => db.Requests.AsNoTracking().SingleAsync(x => x.Operation == "AcceptVersion" && x.ResponseJson.Contains(payload.DecisionId.ToString())));
        var replay = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{payload.OrderId}/versions/1/accept", "factory-a", new RevisionRequest(2), record.Key);
        replay.EnsureSuccessStatusCode(); Assert.Equal(payload.DecisionId, (await replay.Content.ReadFromJsonAsync<OrderOperationResult>())!.DecisionId);
        Assert.Equal(1, await f.Db(db => db.Set<OutboxMessage>().CountAsync(x => x.FactId == payload.DecisionId)));
        Assert.Equal("Pending", (await Status(payload.OrderId)).Status);
    }
    [Fact] public async Task AcceptRollbackRemovesDecisionOutboxLinkAuditAndRequestClaim()
    {
        var createdResponse = await f.Send(HttpMethod.Post, "/api/purchase-orders", body: ApiFixture.Draft(), key: Guid.NewGuid().ToString());
        var id = (await createdResponse.Content.ReadFromJsonAsync<CreatedOrder>())!.OrderId;
        (await f.Send(HttpMethod.Post, $"/api/purchase-orders/{id}/submissions", body: new RevisionRequest(1), key: Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        var key = Guid.NewGuid().ToString(); f.Failure.Armed = 1;
        var failed = await f.Send(HttpMethod.Post, $"/api/purchase-orders/{id}/versions/1/accept", "factory-a", new RevisionRequest(2), key);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await f.Db(async db =>
        {
            Assert.Equal("Submitted", (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
            Assert.Null((await db.Versions.AsNoTracking().SingleAsync(x => x.OrderId == id)).DecisionId);
            Assert.False(await db.Set<ProductionTaskLink>().AnyAsync(x => x.OrderId == id));
            Assert.False(await db.Requests.AnyAsync(x => x.Key == key));
            Assert.False(await db.Audit.AnyAsync(x => x.OrderId == id && x.Action == "AcceptVersion")); return true;
        });
        (await f.Send(HttpMethod.Post, $"/api/purchase-orders/{id}/versions/1/accept", "factory-a", new RevisionRequest(2), key)).EnsureSuccessStatusCode();
        Assert.Equal("Pending", (await Status(id)).Status);
    }
    [Fact] public async Task DuplicateMessageAndDifferentMessageIdCreateOneTaskAuditAndReceipt()
    {
        var accepted = await Accepted(); await Production(accepted); await Production(accepted);
        await Production(accepted with { MessageId = Guid.NewGuid() });
        await ProductionDb(async db =>
        {
            Assert.Equal(1, await db.Tasks.CountAsync(x => x.DecisionId == accepted.FactId));
            Assert.Equal(1, await db.Set<ProductionAudit>().CountAsync(x => x.DecisionId == accepted.FactId));
            var task = await db.Tasks.Include(x => x.Lines).SingleAsync(x => x.DecisionId == accepted.FactId);
            Assert.Equal(2, task.Lines.Count); Assert.Equal(150, task.Lines.Sum(x => x.ConfirmedQuantity));
            Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(x => x.FactId == task.Id)); return true;
        });
    }
    [Fact] public async Task SameMessageIdDifferentContentCannotOverwriteSuccessfulInbox()
    {
        var accepted = await Accepted(); await Production(accepted); var body = accepted.Read<PurchaseOrderAcceptedV1>();
        var changed = body with { Lines = [body.Lines[0] with { Quantity = 999 }, body.Lines[1]] };
        await Production(accepted with { Data = JsonSerializer.SerializeToElement(changed, ContractJson.Options) });
        await ProductionDb(async db =>
        {
            Assert.Equal("Processed", (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == accepted.MessageId)).Status);
            Assert.Equal("MessageContentConflict", (await db.Set<MessageFailure>().SingleAsync(x => x.MessageId == accepted.MessageId)).ErrorCode);
            Assert.Equal(150, (await db.Tasks.Include(x => x.Lines).SingleAsync(x => x.DecisionId == accepted.FactId)).Lines.Sum(x => x.ConfirmedQuantity)); return true;
        });
    }
    [Fact] public async Task NewMessageIdSameDecisionDifferentContentIsBusinessConflict()
    {
        var accepted = await Accepted(); await Production(accepted); var body = accepted.Read<PurchaseOrderAcceptedV1>();
        var changed = accepted with { MessageId = Guid.NewGuid(), Data = JsonSerializer.SerializeToElement(body with { DeliveryDate = body.DeliveryDate.AddDays(1) }, ContractJson.Options) };
        await Production(changed);
        Assert.Equal("Blocked", await ProductionDb(async db => (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == changed.MessageId)).Status));
        Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.OrderId == body.OrderId)));
    }
    [Fact] public async Task ConcurrentDifferentMessageIdsAreArbitratedByPostgreSqlUniqueness()
    {
        var accepted = await Accepted(); var messages = Enumerable.Range(0, 8).Select(_ => accepted with { MessageId = Guid.NewGuid() }).ToArray();
        await Task.WhenAll(messages.Select(Production));
        using var worker = Worker<ProductionDbContext>(f.ProductionFactory.Services);
        await Until(async () =>
        {
            await worker.RetryPending(default);
            var ids = messages.Select(x => x.MessageId).ToArray();
            return await ProductionDb(db => db.Set<InboxMessage>().CountAsync(x => ids.Contains(x.MessageId) && x.Status == "Processed")) == ids.Length;
        });
        Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.DecisionId == accepted.FactId)));
        Assert.Equal(1, await ProductionDb(db => db.Set<ProductionAudit>().CountAsync(x => x.DecisionId == accepted.FactId)));
        foreach (var message in messages) Assert.Equal("Processed", await ProductionDb(async db => (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == message.MessageId)).Status));
    }
    [Fact] public async Task ProductionSaveFailureLeavesPendingThenRecoversAtomically()
    {
        var accepted = await Accepted(); f.ProductionFailure.Armed = 1; await Production(accepted);
        Assert.False(await ProductionDb(db => db.Tasks.AnyAsync(x => x.DecisionId == accepted.FactId)));
        Assert.Equal("Pending", await ProductionDb(async db => (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == accepted.MessageId)).Status));
        await Production(accepted); var receipt = await CreatedReceipt(accepted); await Receipt(receipt);
        Assert.Equal("Created", (await Status(accepted.Read<PurchaseOrderAcceptedV1>().OrderId)).Status);
    }
    [Fact] public async Task ReceiptIsIdempotentAndDoesNotModifyOrderRevision()
    {
        var accepted = await Accepted(); var payload = accepted.Read<PurchaseOrderAcceptedV1>(); var receipt = await CreatedReceipt(accepted);
        Assert.Equal("Pending", (await Status(payload.OrderId)).Status); await Receipt(receipt); await Receipt(receipt);
        await Receipt(receipt with { MessageId = Guid.NewGuid() });
        Assert.Equal("Created", (await Status(payload.OrderId)).Status);
        Assert.Equal(payload.AcceptedRevision, await f.Db(async db => (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == payload.OrderId)).Revision));
        var wrong = receipt.Read<ProductionTaskCreatedV1>() with { FactoryId = DemoSeed.FactoryB };
        var bad = receipt with { MessageId = Guid.NewGuid(), Data = JsonSerializer.SerializeToElement(wrong, ContractJson.Options) };
        await Receipt(bad);
        Assert.Equal("Blocked", await f.Db(async db => (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == bad.MessageId)).Status));
        Assert.Equal("Created", (await Status(payload.OrderId)).Status);
    }
    [Fact] public async Task BlockedThenCreatedAndLateFailureNeverDowngradeCreated()
    {
        var accepted = await Accepted(); var payload = accepted.Read<PurchaseOrderAcceptedV1>();
        var failure = MessageEnvelope.Create(new ProductionTaskCreationFailedV1(payload.OrderId, 1, payload.FactoryId,
            payload.DecisionId, payload.ContentHash(), "InvalidAcceptedOrder"), Guid.NewGuid(), "Production", 1, DateTimeOffset.UtcNow, accepted);
        await Receipt(failure); Assert.Equal("Blocked", (await Status(payload.OrderId)).Status);
        await Receipt(await CreatedReceipt(accepted)); await Receipt(failure with { MessageId = Guid.NewGuid() });
        Assert.Equal("Created", (await Status(payload.OrderId)).Status);
    }
    [Fact] public async Task MissingPrerequisiteWaitsForBackfillAndRepeatBackfillPreservesHistory()
    {
        var accepted = await Accepted(); var payload = accepted.Read<PurchaseOrderAcceptedV1>(); var receipt = await CreatedReceipt(accepted);
        await f.Db(async db =>
        {
            await db.Set<ProductionTaskLink>().Where(x => x.OrderId == payload.OrderId).ExecuteDeleteAsync();
            await db.Set<OutboxMessage>().Where(x => x.FactId == payload.DecisionId).ExecuteDeleteAsync(); return true;
        });
        Assert.Equal("NotScheduled", (await Status(payload.OrderId)).Status); await Receipt(receipt);
        Assert.Equal("Pending", await f.Db(async db => (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == receipt.MessageId)).Status));
        Assert.True(await f.Db(db => ProductionTaskBackfill.Run(db, true)) >= 1);
        Assert.Equal(0, await f.Db(db => ProductionTaskBackfill.Run(db, true))); await Receipt(receipt);
        Assert.Equal("Created", (await Status(payload.OrderId)).Status);
        Assert.Equal(payload.AcceptedRevision, await f.Db(async db => (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == payload.OrderId)).Revision));
    }
    [Fact] public async Task FactoryIsolationAndJwtValidationApplyAtBothServices()
    {
        var accepted = await Accepted(); var payload = accepted.Read<PurchaseOrderAcceptedV1>(); var receipt = await CreatedReceipt(accepted); await Receipt(receipt);
        var id = receipt.Read<ProductionTaskCreatedV1>().TaskId;
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(HttpMethod.Get, $"/api/production-tasks/{id}", "factory-b", client: f.ProductionClient)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Send(HttpMethod.Get, $"/api/production-tasks/{id}", "factory-a", client: f.ProductionClient)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Send(HttpMethod.Get, $"/api/production-tasks/{id}", null, client: f.ProductionClient)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send(HttpMethod.Get, $"/api/production-tasks/{id}", "quality", client: f.ProductionClient)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send(HttpMethod.Get, $"/api/factory/orders/{payload.OrderId}/versions/1/production-task", "factory-b")).StatusCode);
        var b = await f.Send(HttpMethod.Get, $"/api/production-tasks?factoryId={DemoSeed.FactoryA}", "factory-b", client: f.ProductionClient);
        Assert.DoesNotContain(id, (await b.Content.ReadFromJsonAsync<TaskPage>())!.Items.Select(x => x.Id));
    }
    [Fact] public async Task ApplicationDatabaseAccountsCannotConnectToOtherService()
    {
        var builder = new NpgsqlConnectionStringBuilder(f.Connection) { Database = "scm_production_test", Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync()); Assert.Equal("42501", error.SqlState);
        builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SCM_PRODUCTION_TEST_CONNECTION")) { Database = "scm_procurement_test", Pooling = false };
        await using var reverse = new NpgsqlConnection(builder.ConnectionString);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => reverse.OpenAsync())).SqlState);
    }
    [Fact] public async Task InvalidContractIsQuarantinedWithoutTaskOrInfiniteRetry()
    {
        var accepted = await Accepted(); var bad = accepted with { MessageId = Guid.NewGuid(), ContractVersion = 99 };
        await Production(bad);
        Assert.True(await ProductionDb(db => db.Set<MessageFailure>().AnyAsync(x => x.MessageId == bad.MessageId && x.ErrorCode == "InvalidEnvelope")));
        Assert.False(await ProductionDb(db => db.Tasks.AnyAsync(x => x.DecisionId == accepted.FactId)));
    }
    [Fact] public async Task OperatorRetryKeepsInvalidOriginalQuarantinedAndCannotResetProcessedMessage()
    {
        var accepted = await Accepted(); var bad = accepted with { MessageId = Guid.NewGuid(), ContractVersion = 99 };
        await Production(bad);
        using var scope = f.ProductionFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductionDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<MessageProcessor<ProductionDbContext>>();
        var consumer = scope.ServiceProvider.GetRequiredService<MessagingOptions>().ConsumerName;
        await MessagingAdministration.InspectOrRetry(db, consumer, bad.MessageId, processor);
        Assert.Equal(bad.Serialize(), (await db.Set<MessageFailure>().SingleAsync(x => x.MessageId == bad.MessageId)).EnvelopeJson);
        Assert.False(await db.Tasks.AnyAsync(x => x.DecisionId == accepted.FactId));
        await processor.Process(accepted.Serialize(), default);
        await MessagingAdministration.InspectOrRetry(db, consumer, accepted.MessageId, processor);
        Assert.Equal("Processed", (await db.Set<InboxMessage>().SingleAsync(x => x.MessageId == accepted.MessageId)).Status);
        Assert.Equal(1, await db.Tasks.CountAsync(x => x.DecisionId == accepted.FactId));
    }
    [Fact] public async Task RealBrokerRoundTripAndInitiallyUnavailableConnectionRecover()
    {
        var accepted = await Accepted(); var payload = accepted.Read<PurchaseOrderAcceptedV1>();
        var options = f.Factory.Services.GetRequiredService<MessagingOptions>(); var originalPort = options.Port; options.Port = 1;
        using var procurement = Worker<ProcurementDbContext>(f.Factory.Services); using var production = Worker<ProductionDbContext>(f.ProductionFactory.Services);
        try
        {
            await procurement.StartAsync(default); await Task.Delay(500);
            Assert.Equal("Pending", (await Status(payload.OrderId)).Status); Assert.False(f.Factory.Services.GetRequiredService<MessagingState>().Connected);
            options.Port = originalPort; await production.StartAsync(default);
            await Until(async () => (await Status(payload.OrderId)).Status == "Created");
            var taskId = (await Status(payload.OrderId)).TaskId;
            Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.Id == taskId)));
            Assert.NotNull(await f.Db(async db => (await db.Set<OutboxMessage>().SingleAsync(x => x.FactId == payload.DecisionId)).SentAt));
        }
        finally { options.Port = originalPort; await procurement.StopAsync(default); await production.StopAsync(default); }
    }
    [Fact] public async Task RealBrokerUnroutablePublishIsNotMarkedSentAndRecoversAfterBinding()
    {
        var accepted = await Accepted(); await using var connection = await Broker();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueueUnbindAsync("scm.production.accepted-orders", "scm.procurement.events", nameof(PurchaseOrderAcceptedV1));
        using var worker = Worker<ProcurementDbContext>(f.Factory.Services);
        try
        {
            await worker.PublishPending(channel, default);
            Assert.Null(await f.Db(async db => (await db.Set<OutboxMessage>().SingleAsync(x => x.MessageId == accepted.MessageId)).SentAt));
            await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.PublishReturnException>(() => MessagingWorker<ProcurementDbContext>.Send(channel, "scm.procurement.events", accepted, default));
        }
        finally { await channel.QueueBindAsync("scm.production.accepted-orders", "scm.procurement.events", nameof(PurchaseOrderAcceptedV1)); }
        await Task.Delay(150); await worker.PublishPending(channel, default);
        Assert.NotNull(await f.Db(async db => (await db.Set<OutboxMessage>().SingleAsync(x => x.MessageId == accepted.MessageId)).SentAt));
    }
    [Fact] public async Task RealBrokerCommitBeforeAckChannelLossRedeliversSafely()
    {
        var accepted = await Accepted(); await using var connection = await Broker();
        await using (var send = await connection.CreateChannelAsync(new CreateChannelOptions(true, true)))
            await MessagingWorker<ProcurementDbContext>.Send(send, "scm.procurement.events", accepted, default);
        // 专用测试队列中可能有其他测试事件；只找本次消息并处理无关的旧消息。
        await using (var first = await connection.CreateChannelAsync())
        {
            while (true)
            {
                var delivery = await first.BasicGetAsync("scm.production.accepted-orders", false); Assert.NotNull(delivery);
                var message = JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span, ContractJson.Options)!;
                await Production(message);
                if (message.MessageId == accepted.MessageId) break;
                await first.BasicAckAsync(delivery.DeliveryTag, false);
            }
            await first.CloseAsync(); // 任务事务已提交，故意不 ack，真实 broker 重新投递。
        }
        await using var second = await connection.CreateChannelAsync();
        await Until(async () =>
        {
            var delivery = await second.BasicGetAsync("scm.production.accepted-orders", false); if (delivery is null) return false;
            var message = JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span, ContractJson.Options)!;
            await Production(message); await second.BasicAckAsync(delivery.DeliveryTag, false);
            if (message.MessageId != accepted.MessageId) return false;
            Assert.True(delivery.Redelivered); return true;
        });
        Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.DecisionId == accepted.FactId)));
        Assert.Equal(1, await ProductionDb(db => db.Set<ProductionAudit>().CountAsync(x => x.DecisionId == accepted.FactId)));
    }
    [Fact] public async Task RealBrokerConfirmBeforeSentMarkerFailurePublishesAgainSafely()
    {
        await Accepted();
        var outgoing = await f.Db(db => db.Set<OutboxMessage>().AsNoTracking().Where(x => x.SentAt == null).OrderBy(x => x.EnqueuedAt).FirstAsync());
        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(outgoing.EnvelopeJson, ContractJson.Options)!;
        await using var connection = await Broker(); await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.QueuePurgeAsync("scm.production.accepted-orders"); // 只清理专用测试队列。
        using var worker = Worker<ProcurementDbContext>(f.Factory.Services); f.Failure.Armed = 1;
        await worker.PublishPending(channel, default);
        Assert.Null(await f.Db(async db => (await db.Set<OutboxMessage>().SingleAsync(x => x.MessageId == envelope.MessageId)).SentAt));
        await Task.Delay(150); await worker.PublishPending(channel, default);
        Assert.NotNull(await f.Db(async db => (await db.Set<OutboxMessage>().SingleAsync(x => x.MessageId == envelope.MessageId)).SentAt));
        var copies = 0;
        await Until(async () =>
        {
            var delivery = await channel.BasicGetAsync("scm.production.accepted-orders", false); if (delivery is null) return false;
            var message = JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span, ContractJson.Options)!;
            await Production(message); await channel.BasicAckAsync(delivery.DeliveryTag, false);
            if (message.MessageId == envelope.MessageId) copies++;
            return copies >= 2;
        });
        Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.DecisionId == envelope.FactId)));
    }
    [Fact] public async Task RealBrokerDatabaseUnavailableDoesNotAckAndRecovers()
    {
        var accepted = await Accepted(); await using var connection = await Broker();
        await using var send = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await send.QueuePurgeAsync("scm.production.accepted-orders");
        await MessagingWorker<ProcurementDbContext>.Send(send, "scm.procurement.events", accepted, default);
        await using (var first = await connection.CreateChannelAsync())
        {
            var delivery = await first.BasicGetAsync("scm.production.accepted-orders", false); Assert.NotNull(delivery);
            var badConnection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SCM_PRODUCTION_TEST_CONNECTION")) { Port = 1, Timeout = 1, Pooling = false };
            await using var unavailable = new ProductionDbContext(new DbContextOptionsBuilder<ProductionDbContext>().UseNpgsql(badConnection.ConnectionString).Options);
            var handler = new ProductionMessageHandler(unavailable, new ProductionService(new ProductionStore(unavailable), TimeProvider.System));
            var processor = new MessageProcessor<ProductionDbContext>(unavailable, handler, f.ProductionFactory.Services.GetRequiredService<MessagingOptions>());
            await Assert.ThrowsAsync<NpgsqlException>(() => processor.Process(Encoding.UTF8.GetString(delivery.Body.Span), default));
            await first.CloseAsync(); // 无法落库时没有 ack，broker 保留消息。
        }
        await using var recovered = await connection.CreateChannelAsync();
        await Until(async () =>
        {
            var delivery = await recovered.BasicGetAsync("scm.production.accepted-orders", false); if (delivery is null) return false;
            Assert.True(delivery.Redelivered); await Production(accepted); await recovered.BasicAckAsync(delivery.DeliveryTag, false); return true;
        });
        Assert.Equal(1, await ProductionDb(db => db.Tasks.CountAsync(x => x.DecisionId == accepted.FactId)));
    }
}
