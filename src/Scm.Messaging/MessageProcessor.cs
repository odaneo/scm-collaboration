using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scm.IntegrationContracts;

namespace Scm.Messaging;

public sealed class MessageProcessor<TDb>(TDb db, IIntegrationMessageHandler handler, MessagingOptions options)
    where TDb : DbContext
{
    public async Task Process(string raw, CancellationToken ct)
    {
        MessageEnvelope? envelope = null;
        string hash;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope>(raw, ContractJson.Options);
            if (envelope is null || envelope.MessageId == Guid.Empty || envelope.FactId == Guid.Empty ||
                envelope.ContractVersion != 1 || envelope.SourceRevision < 1 || envelope.OccurredAtUtc == default ||
                string.IsNullOrWhiteSpace(envelope.CorrelationId)) throw new InvalidMessage("InvalidEnvelope");
            hash = ContractJson.Hash(JsonSerializer.SerializeToElement(envelope, ContractJson.Options));
        }
        catch (Exception ex) when (ex is JsonException or InvalidMessage)
        {
            hash = ContractJson.RawHash(raw);
            await SaveFailure(envelope?.MessageId ?? new Guid(Convert.FromHexString(hash)[..16]), hash, raw, "InvalidEnvelope", ct);
            return;
        }
        var id = envelope.MessageId;
        using var activity = new Activity("consume " + envelope.EventType);
        if (ActivityContext.TryParse(envelope.TraceParent, null, out _)) activity.SetParentId(envelope.TraceParent!);
        activity.AddTag("messaging.message.id", id).AddTag("correlation.id", envelope.CorrelationId).Start();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inbox_messages ("ConsumerName", "MessageId", "Hash", "EnvelopeJson", "Status", "Attempts", "NextAttemptAt")
                VALUES ({options.ConsumerName}, {id}, {hash}, {raw}, 'Pending', 0, {now})
                ON CONFLICT ("ConsumerName", "MessageId") DO NOTHING
                """, ct);
            var inbox = await db.Set<InboxMessage>().FromSqlInterpolated($"""
                SELECT * FROM inbox_messages WHERE "ConsumerName" = {options.ConsumerName} AND "MessageId" = {id} FOR UPDATE
                """).SingleAsync(ct);
            if (inbox.Hash != hash)
            {
                await SaveFailure(id, hash, raw, "MessageContentConflict", ct);
                await transaction.CommitAsync(ct);
                return;
            }
            if (inbox.Status is "Processed" or "Blocked") { await transaction.CommitAsync(ct); return; }
            var error = await handler.Handle(envelope, ct);
            inbox.Attempts++;
            inbox.Status = error is null ? "Processed" : "Blocked";
            inbox.ProcessedAt = error is null ? now : null;
            inbox.LastError = error;
            if (error is not null) await AddFailure(id, hash, raw, error, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 原事务已回滚；持久 Pending 只代表保管消息，绝不代表业务成功。
            db.ChangeTracker.Clear();
            var error = ex is InvalidMessage ? ex.Message : ex is JsonException ? "InvalidPayload" : ex is MissingPrerequisite ? "MissingPrerequisite" : ex.GetType().Name;
            var status = ex is InvalidMessage or JsonException ? "Blocked" : "Pending";
            var next = DateTimeOffset.UtcNow.AddMilliseconds(options.RetryMilliseconds);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inbox_messages ("ConsumerName", "MessageId", "Hash", "EnvelopeJson", "Status", "Attempts", "NextAttemptAt", "LastError")
                VALUES ({options.ConsumerName}, {id}, {hash}, {raw}, {status}, 1, {next}, {error})
                ON CONFLICT ("ConsumerName", "MessageId") DO UPDATE
                SET "Status" = {status}, "Attempts" = inbox_messages."Attempts" + 1, "NextAttemptAt" = {next}, "LastError" = {error}
                WHERE inbox_messages."Hash" = {hash} AND inbox_messages."Status" = 'Pending'
                """, ct);
            if (status == "Blocked") await SaveFailure(id, hash, raw, error, ct);
            // 保存失败则向调用者抛出：不能 ack，关闭消费通道后让 broker 重投。
        }
    }
    private async Task AddFailure(Guid id, string hash, string raw, string code, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO message_failures ("Id", "ConsumerName", "MessageId", "Hash", "EnvelopeJson", "ErrorCode", "OccurredAt")
            VALUES ({Guid.NewGuid()}, {options.ConsumerName}, {id}, {hash}, {raw}, {code}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("ConsumerName", "MessageId", "Hash") DO NOTHING
            """, ct);
    private Task SaveFailure(Guid id, string hash, string raw, string code, CancellationToken ct) => AddFailure(id, hash, raw, code, ct);
}
