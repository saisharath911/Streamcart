using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace StreamCart.BuildingBlocks.Observability;

public static class Telemetry
{
    public const string SourceName = "StreamCart.Messaging";
    public const string MeterName = "StreamCart";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagesProcessed =
        Meter.CreateCounter<long>("streamcart.messages.processed", description: "Messages consumed, tagged by type and result.");

    public static readonly Counter<long> MessagesFailed =
        Meter.CreateCounter<long>("streamcart.messages.failed", description: "Handler failures that will be retried by SQS.");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("streamcart.outbox.published", description: "Outbox rows successfully published.");

    public static readonly Counter<long> OutboxPublishFailed =
        Meter.CreateCounter<long>("streamcart.outbox.publish_failed", description: "Outbox publish attempts that failed.");
}
