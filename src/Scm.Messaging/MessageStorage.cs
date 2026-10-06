using Microsoft.EntityFrameworkCore;
using Scm.IntegrationContracts;

namespace Scm.Messaging;

public sealed class OutboxMessage
{
    public Guid MessageId { get; set; }
    public string EventType { get; set; } = "";
    public Guid FactId { get; set; }
    public string EnvelopeJson { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public static OutboxMessage From(MessageEnvelope envelope, string? contentHash = null) => new()
    {
        MessageId = envelope.MessageId, EventType = envelope.EventType, FactId = envelope.FactId,
        EnvelopeJson = envelope.Serialize(), ContentHash = contentHash ?? ContractJson.Hash(envelope.Data),
        EnqueuedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow
    };
}
public sealed class InboxMessage
{
    public string ConsumerName { get; set; } = "";
    public Guid MessageId { get; set; }
    public string Hash { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
public sealed class MessageFailure
{
    public Guid Id { get; set; }
    public string ConsumerName { get; set; } = "";
    public Guid MessageId { get; set; }
    public string Hash { get; set; } = "";
    public string EnvelopeJson { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
public static class MessageStorage
{
    public static void MapMessages(this ModelBuilder model)
    {
        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).ValueGeneratedNever();
            e.Property(x => x.EventType).HasMaxLength(100); e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(100); e.HasIndex(x => new { x.EventType, x.FactId }).IsUnique();
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
        });
        model.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages", t => t.HasCheckConstraint("ck_inbox_status", "\"Status\" IN ('Pending','Processed','Blocked')"));
            e.HasKey(x => new { x.ConsumerName, x.MessageId }); e.Property(x => x.ConsumerName).HasMaxLength(100);
            e.Property(x => x.Hash).HasMaxLength(64); e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(100); e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
        model.Entity<MessageFailure>(e =>
        {
            e.ToTable("message_failures"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ConsumerName).HasMaxLength(100); e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasMaxLength(100); e.HasIndex(x => new { x.ConsumerName, x.MessageId, x.Hash }).IsUnique();
        });
    }
}
public sealed class InvalidMessage(string code) : Exception(code);
public sealed class MissingPrerequisite : Exception;
public interface IIntegrationMessageHandler
{
    // 在 MessageProcessor 打开的本地事务中执行，不自行确认 broker 消息。
    Task<string?> Handle(MessageEnvelope envelope, CancellationToken ct);
}
