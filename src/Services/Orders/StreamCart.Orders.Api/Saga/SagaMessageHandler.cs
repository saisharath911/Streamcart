using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Orders.Api.Data;
using StreamCart.Orders.Api.Domain;
using StreamCart.Orders.Api.Realtime;

namespace StreamCart.Orders.Api.Saga;

/// <summary>
/// Thin adapter between the transport and the pure <see cref="OrderSaga"/>: load state,
/// ask the saga what to do, persist the decision and enqueue outgoing messages. The inbox
/// processor wraps this in a single transaction.
/// </summary>
internal sealed class SagaMessageHandler<TMessage>(
    OrdersDbContext db,
    IOutbox outbox,
    IOrderNotifier notifier,
    TimeProvider clock,
    ILogger<SagaMessageHandler<TMessage>> logger) : MessageHandler<TMessage>
    where TMessage : IIntegrationMessage
{
    protected override async Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == message.OrderId, cancellationToken);

        if (order is null)
        {
            // Cannot happen with the outbox (orders are committed before commands are sent),
            // but a stray message must not poison the queue.
            logger.LogWarning("Received {Type} for unknown order {OrderId}; ignoring.", typeof(TMessage).Name, message.OrderId);
            return;
        }

        var decision = OrderSaga.Decide(order.ToSnapshot(), message);

        if (message is InventoryReserved reserved && decision.Status == OrderStatus.AwaitingPayment)
        {
            order.ApplyPricing(reserved.Lines);
        }

        var entry = order.Apply(decision, OrderSaga.TriggerName(message), clock.GetUtcNow());
        foreach (var outgoing in decision.Outgoing)
        {
            outbox.Enqueue(outgoing);
        }

        logger.LogInformation("Order {OrderId}: {Trigger} -> {Status}. {Narrative}",
            order.Id, entry.Trigger, order.Status, decision.Narrative);

        var summary = order.ToSummary();
        var entryDto = entry.ToDto();
        context.OnCommitted(ct => notifier.OrderChangedAsync(summary, entryDto, ct));
    }
}

internal static class SagaRegistration
{
    public static IServiceCollection AddOrderSaga(this IServiceCollection services)
    {
        services.AddMessageHandler<InventoryReserved, SagaMessageHandler<InventoryReserved>>();
        services.AddMessageHandler<InventoryRejected, SagaMessageHandler<InventoryRejected>>();
        services.AddMessageHandler<InventoryReleased, SagaMessageHandler<InventoryReleased>>();
        services.AddMessageHandler<PaymentSucceeded, SagaMessageHandler<PaymentSucceeded>>();
        services.AddMessageHandler<PaymentFailed, SagaMessageHandler<PaymentFailed>>();
        services.AddMessageHandler<PaymentRefunded, SagaMessageHandler<PaymentRefunded>>();
        services.AddHostedService<SagaTimeoutWatcher>();
        return services;
    }
}
