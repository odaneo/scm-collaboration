using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scm.IntegrationContracts;

// 只共享线上契约，不共享聚合、领域规则或数据库模型。
public sealed record AcceptedLine(Guid LineId, Guid SkuId, string Style, string Color, string Size, int Quantity);
public sealed record PurchaseOrderAcceptedV1(Guid OrderId, int AcceptedOrderVersion, Guid FactoryId,
    string FactoryName, Guid DecisionId, int AcceptedRevision, DateTimeOffset AcceptedAtUtc,
    DateOnly DeliveryDate, IReadOnlyList<AcceptedLine> Lines)
{
    public PurchaseOrderAcceptedV1 Normalize() => this with
    {
        AcceptedAtUtc = ContractJson.UtcMicroseconds(AcceptedAtUtc),
        Lines = Lines.OrderBy(x => x.LineId).ToArray()
    };
    public string ContentHash() => ContractJson.Hash(JsonSerializer.SerializeToElement(Normalize(), ContractJson.Options));
}
public sealed record ProductionTaskCreatedV1(Guid OrderId, int AcceptedOrderVersion, Guid FactoryId,
    Guid DecisionId, string AcceptedContentHash, Guid TaskId, DateTimeOffset CreatedAtUtc);
public sealed record ProductionTaskCreationFailedV1(Guid OrderId, int AcceptedOrderVersion, Guid FactoryId,
    Guid DecisionId, string AcceptedContentHash, string ErrorCode);

public sealed record MessageEnvelope(Guid MessageId, string EventType, int ContractVersion, Guid FactId,
    string SourceService, int SourceRevision, DateTimeOffset OccurredAtUtc, string CorrelationId,
    Guid? CausationId, string? TraceParent, JsonElement Data)
{
    public static MessageEnvelope Create<T>(T data, Guid factId, string source, int revision,
        DateTimeOffset at, MessageEnvelope? cause = null) => new(Guid.NewGuid(), typeof(T).Name, 1,
        factId, source, revision, ContractJson.UtcMicroseconds(at), cause?.CorrelationId ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N"), cause?.MessageId, Activity.Current?.Id ?? cause?.TraceParent,
        JsonSerializer.SerializeToElement(data, ContractJson.Options));
    public T Read<T>() => Data.Deserialize<T>(ContractJson.Options)
        ?? throw new JsonException("事件内容为空。");
    public string Serialize() => JsonSerializer.Serialize(this, ContractJson.Options);
}

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    // PostgreSQL timestamptz 精度为微秒；使首次事件与从已落库事实补录的摘要一致。
    public static DateTimeOffset UtcMicroseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    public static string Hash(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static string RawHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
