namespace Procurement.Domain;

// 内部领域事实；由应用层映射为独立集成事件，领域不认识 Outbox 或 RabbitMQ。
public sealed record OrderVersionAccepted(Guid OrderId, int Version, Guid FactoryId, Guid DecisionId,
    int Revision, DateTimeOffset OccurredAtUtc);
