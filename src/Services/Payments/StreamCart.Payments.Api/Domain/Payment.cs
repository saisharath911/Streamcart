namespace StreamCart.Payments.Api.Domain;

public enum PaymentStatus
{
    Captured,
    Declined,
    Refunded,
}

public sealed class Payment
{
    private Payment()
    {
        CustomerId = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Payment Create(Guid orderId, string customerId, decimal amount, PaymentDecision decision, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        CustomerId = customerId,
        Amount = amount,
        Status = decision.Approved ? PaymentStatus.Captured : PaymentStatus.Declined,
        Reason = decision.DeclineReason,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public bool TryRefund(string reason, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Captured)
        {
            return false;
        }

        Status = PaymentStatus.Refunded;
        Reason = reason;
        UpdatedAt = now;
        return true;
    }
}
