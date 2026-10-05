using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Payments.Api.Domain;

namespace StreamCart.Payments.Api.Chaos;

/// <summary>Thread-safe holder for the live chaos configuration.</summary>
public sealed class ChaosState
{
    private ChaosConfig _current = ChaosConfig.Calm;

    public ChaosConfig Current => Volatile.Read(ref _current);

    public ChaosConfig Set(ChaosConfig config)
    {
        var normalized = config.Normalize();
        Volatile.Write(ref _current, normalized);
        return normalized;
    }
}

/// <summary>Applies the chaos configuration to this service's message consumer.</summary>
internal sealed class PaymentsDeliveryChaos(ChaosState state, ILogger<PaymentsDeliveryChaos> logger) : IDeliveryChaos
{
    private static readonly string ProcessPaymentType = MessageTypeRegistry.NameOf<ProcessPayment>();

    public async ValueTask BeforeProcessingAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var latency = state.Current.LatencyMs;
        if (latency > 0 && envelope.Type == ProcessPaymentType)
        {
            logger.LogInformation("Chaos: simulating a {Latency} ms payment gateway call.", latency);
            await Task.Delay(latency, cancellationToken);
        }
    }

    public bool ShouldSkipAcknowledgement(MessageEnvelope envelope, ProcessingResult result) =>
        // Only "crash" after real work; redeliveries (duplicates) are always acknowledged,
        // otherwise a message could bounce into the dead-letter queue.
        result == ProcessingResult.Handled && Random.Shared.NextDouble() < state.Current.DuplicateDeliveryRate;
}
