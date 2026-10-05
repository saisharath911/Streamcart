using System.Globalization;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using static StreamCart.Orders.Api.Domain.OrderStatus;

namespace StreamCart.Orders.Api.Domain;

public enum OrderStatus
{
    AwaitingInventory,
    AwaitingPayment,
    Confirmed,
    Compensating,
    Cancelled,
}

public enum TimelineKind
{
    Progress,
    Success,
    Compensation,
    Refund,
    Ignored,
}

/// <summary>Raised internally by the timeout watcher; never crosses a service boundary.</summary>
public sealed record SagaTimedOut(Guid OrderId, TimeSpan After);

/// <summary>The minimal state the saga needs to make a decision.</summary>
public sealed record OrderSnapshot(
    Guid OrderId,
    string CustomerId,
    OrderStatus Status,
    decimal? Total,
    string? CancellationReason);

/// <summary>What the saga decided: the next state, messages to send, and a human-readable narrative.</summary>
public sealed record SagaDecision(
    OrderStatus Status,
    IReadOnlyList<IIntegrationMessage> Outgoing,
    string Narrative,
    TimelineKind Kind)
{
    public decimal? Total { get; init; }
    public Guid? PaymentId { get; init; }
    public string? CancellationReason { get; init; }
}

/// <summary>
/// Order fulfilment saga (orchestration style) as a pure function: (state, message) -> decision.
/// No I/O, no clock, no randomness, so every path - including out-of-order, duplicate and
/// late messages - is covered by fast unit tests.
///
///   AwaitingInventory --InventoryReserved--> AwaitingPayment --PaymentSucceeded--> Confirmed
///         |                                        |
///   InventoryRejected                       PaymentFailed / timeout
///         v                                        v
///     Cancelled  <------InventoryReleased------ Compensating
/// </summary>
public static class OrderSaga
{
    public static SagaDecision Start(Guid orderId, IReadOnlyList<OrderLineContract> lines) =>
        new(AwaitingInventory,
            [new ReserveInventory(orderId, lines)],
            $"Order placed with {lines.Count} line(s). Reserving stock.",
            TimelineKind.Progress);

    public static SagaDecision Decide(OrderSnapshot order, object trigger) => (order.Status, trigger) switch
    {
        // ----- Happy path ------------------------------------------------------------
        (AwaitingInventory, InventoryReserved e) =>
            new SagaDecision(AwaitingPayment,
                [new ProcessPayment(order.OrderId, order.CustomerId, e.Total)],
                $"Stock reserved, total {Money(e.Total)}. Requesting payment.",
                TimelineKind.Progress)
            { Total = e.Total },

        (AwaitingPayment, PaymentSucceeded e) =>
            new SagaDecision(Confirmed,
                [new OrderConfirmed(order.OrderId, order.CustomerId, e.Amount)],
                $"Payment of {Money(e.Amount)} captured. Order confirmed.",
                TimelineKind.Success)
            { PaymentId = e.PaymentId },

        // ----- Failures before anything needs undoing ---------------------------------
        (AwaitingInventory, InventoryRejected e) =>
            new SagaDecision(Cancelled,
                [new OrderCancelled(order.OrderId, order.CustomerId, e.Reason)],
                $"Stock unavailable ({e.Reason}). Order cancelled, nothing to compensate.",
                TimelineKind.Compensation)
            { CancellationReason = e.Reason },

        // ----- Failures that require compensation ------------------------------------
        (AwaitingPayment, PaymentFailed e) => Compensate(order, $"Payment failed: {e.Reason}"),
        (AwaitingPayment, SagaTimedOut t) => Compensate(order, $"Payment did not respond within {t.After.TotalSeconds:0}s"),
        // Stock may have been reserved even though we never heard back, so release defensively.
        (AwaitingInventory, SagaTimedOut t) => Compensate(order, $"Inventory did not respond within {t.After.TotalSeconds:0}s"),

        (Compensating, InventoryReleased) =>
            new SagaDecision(Cancelled,
                [new OrderCancelled(order.OrderId, order.CustomerId, order.CancellationReason ?? "Compensated")],
                "Stock released. Order cancelled and fully compensated.",
                TimelineKind.Compensation),

        // ----- Late / out-of-order messages after we gave up -------------------------
        (Compensating or Cancelled, PaymentSucceeded e) =>
            new SagaDecision(order.Status,
                [new RefundPayment(order.OrderId, "Payment captured after the order was cancelled")],
                $"Late payment of {Money(e.Amount)} arrived after cancellation. Issuing refund.",
                TimelineKind.Refund)
            { PaymentId = e.PaymentId },

        (Compensating or Cancelled, InventoryReserved) =>
            new SagaDecision(order.Status,
                [new ReleaseInventory(order.OrderId, "Late reservation for a cancelled order")],
                "Late stock reservation arrived after cancellation. Releasing it.",
                TimelineKind.Compensation),

        (_, PaymentRefunded e) =>
            new SagaDecision(order.Status, [], $"Refund of {Money(e.Amount)} completed.", TimelineKind.Refund),

        // ----- Everything else is a duplicate or stale message: no-op -----------------
        _ => new SagaDecision(order.Status, [],
                $"Ignored {TriggerName(trigger)} while {order.Status} (duplicate or out of order).",
                TimelineKind.Ignored),
    };

    public static bool IsTerminal(OrderStatus status) => status is Confirmed or Cancelled;

    public static string TriggerName(object trigger) => trigger.GetType().Name;

    private static SagaDecision Compensate(OrderSnapshot order, string reason) =>
        new(Compensating,
            [new ReleaseInventory(order.OrderId, reason)],
            $"{reason}. Compensating: releasing reserved stock.",
            TimelineKind.Compensation)
        { CancellationReason = reason };

    private static string Money(decimal amount) =>
        "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
}
