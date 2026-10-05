using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamCart.BuildingBlocks.Messaging;

/// <summary>
/// Transport-agnostic wrapper written to the outbox and published to SNS.
/// Carries routing (<see cref="Type"/>), de-duplication (<see cref="MessageId"/>)
/// and W3C trace context (<see cref="TraceParent"/>) so a single trace spans
/// HTTP request -> outbox -> SNS -> SQS -> handler in another service.
/// </summary>
public sealed record MessageEnvelope(
    Guid MessageId,
    string Type,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    string Payload,
    string? TraceParent = null)
{
    public static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static MessageEnvelope Create(
        IIntegrationMessage message,
        string source,
        TimeProvider clock,
        string? traceParent = null) =>
        new(
            MessageId: Guid.NewGuid(),
            Type: MessageTypeRegistry.NameOf(message.GetType()),
            CorrelationId: message.OrderId,
            Source: source,
            OccurredAt: clock.GetUtcNow(),
            Payload: JsonSerializer.Serialize(message, message.GetType(), SerializerOptions),
            TraceParent: traceParent);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static MessageEnvelope? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageEnvelope>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
