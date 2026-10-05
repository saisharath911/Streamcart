namespace StreamCart.Payments.Api.Domain;

/// <summary>Fault-injection settings controlled from the Saga Lab dashboard.</summary>
public sealed record ChaosConfig(double FailureRate, int LatencyMs, double DuplicateDeliveryRate)
{
    public const int MaxLatencyMs = 25_000;

    public static readonly ChaosConfig Calm = new(0, 0, 0);

    public static readonly IReadOnlyDictionary<string, ChaosConfig> Presets = new Dictionary<string, ChaosConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["calm"] = Calm,
        // 35% of payments are declined -> compensation path (release stock, cancel).
        ["flaky-payments"] = new(0.35, 300, 0),
        // Gateway slower than the saga's 15s step timeout -> timeout, compensation,
        // then the late PaymentSucceeded triggers an automatic refund.
        ["slow-gateway"] = new(0, 20_000, 0),
        // Half of processed messages are "crashed" before ack -> redelivery -> inbox de-dupes.
        ["duplicate-storm"] = new(0, 0, 0.5),
        ["everything-on-fire"] = new(0.25, 4_000, 0.4),
    };

    public ChaosConfig Normalize() => new(
        Math.Clamp(FailureRate, 0, 1),
        Math.Clamp(LatencyMs, 0, MaxLatencyMs),
        Math.Clamp(DuplicateDeliveryRate, 0, 1));
}

public sealed record PaymentDecision(bool Approved, string? DeclineReason)
{
    public static readonly PaymentDecision Approve = new(true, null);

    public static PaymentDecision Decline(string reason) => new(false, reason);
}

/// <summary>
/// Pure payment rules. The random roll is passed in so tests are deterministic.
/// </summary>
public static class PaymentDecider
{
    public const decimal SingleTransactionLimit = 5_000m;

    public static PaymentDecision Decide(decimal amount, ChaosConfig chaos, double roll)
    {
        if (amount <= 0)
        {
            return PaymentDecision.Decline("Invalid amount");
        }

        if (amount > SingleTransactionLimit)
        {
            return PaymentDecision.Decline("Exceeds the $5,000 single-transaction limit");
        }

        if (roll < chaos.FailureRate)
        {
            return PaymentDecision.Decline("Card declined by issuer (chaos)");
        }

        return PaymentDecision.Approve;
    }
}
