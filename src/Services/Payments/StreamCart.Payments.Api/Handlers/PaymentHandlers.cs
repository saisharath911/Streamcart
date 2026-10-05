using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Payments.Api.Chaos;
using StreamCart.Payments.Api.Data;
using StreamCart.Payments.Api.Domain;

namespace StreamCart.Payments.Api.Handlers;

internal sealed class ProcessPaymentHandler(
    PaymentsDbContext db,
    IOutbox outbox,
    ChaosState chaos,
    TimeProvider clock,
    ILogger<ProcessPaymentHandler> logger) : MessageHandler<ProcessPayment>
{
    protected override async Task HandleAsync(ProcessPayment command, MessageContext context, CancellationToken ct)
    {
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == command.OrderId, ct);
        if (existing is not null)
        {
            // Never charge twice: replay the original outcome instead.
            outbox.Enqueue(existing.Status switch
            {
                PaymentStatus.Captured => new PaymentSucceeded(existing.OrderId, existing.Id, existing.Amount),
                PaymentStatus.Refunded => new PaymentFailed(existing.OrderId, "Payment was already refunded"),
                _ => new PaymentFailed(existing.OrderId, existing.Reason ?? "Declined"),
            });
            logger.LogInformation("Payment for {OrderId} already {Status}; replayed outcome.", command.OrderId, existing.Status);
            return;
        }

        var decision = PaymentDecider.Decide(command.Amount, chaos.Current, Random.Shared.NextDouble());
        var payment = Payment.Create(command.OrderId, command.CustomerId, command.Amount, decision, clock.GetUtcNow());
        db.Payments.Add(payment);

        outbox.Enqueue(decision.Approved
            ? new PaymentSucceeded(payment.OrderId, payment.Id, payment.Amount)
            : new PaymentFailed(payment.OrderId, decision.DeclineReason ?? "Declined"));

        logger.LogInformation("Payment for {OrderId} {Status} ({Amount}).", command.OrderId, payment.Status, command.Amount);
    }
}

internal sealed class RefundPaymentHandler(
    PaymentsDbContext db,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<RefundPaymentHandler> logger) : MessageHandler<RefundPayment>
{
    protected override async Task HandleAsync(RefundPayment command, MessageContext context, CancellationToken ct)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == command.OrderId, ct);
        if (payment is null)
        {
            logger.LogWarning("Refund requested for {OrderId} but no payment exists.", command.OrderId);
            return;
        }

        if (payment.TryRefund(command.Reason, clock.GetUtcNow()))
        {
            outbox.Enqueue(new PaymentRefunded(payment.OrderId, payment.Id, payment.Amount));
            logger.LogInformation("Refunded {Amount} for {OrderId}: {Reason}", payment.Amount, command.OrderId, command.Reason);
        }
    }
}
