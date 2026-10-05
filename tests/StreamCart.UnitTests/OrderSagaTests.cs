using StreamCart.BuildingBlocks.Contracts;
using StreamCart.Orders.Api.Domain;

namespace StreamCart.UnitTests;

public class OrderSagaTests
{
    private static readonly Guid OrderId = Guid.Parse("0192f0c4-7a10-7000-8000-000000000001");
    private static readonly Guid PaymentId = Guid.Parse("0192f0c4-7a10-7000-8000-0000000000ff");

    private static OrderSnapshot Order(OrderStatus status, decimal? total = 298m, string? reason = null) =>
        new(OrderId, "cust-42", status, total, reason);

    private static InventoryReserved Reserved(decimal total = 298m) =>
        new(OrderId, [new PricedLineContract("KB-75", 2, 149m)], total);

    [Fact]
    public void Start_requests_a_stock_reservation()
    {
        var decision = OrderSaga.Start(OrderId, [new OrderLineContract("KB-75", 2)]);

        Assert.Equal(OrderStatus.AwaitingInventory, decision.Status);
        var command = Assert.IsType<ReserveInventory>(Assert.Single(decision.Outgoing));
        Assert.Equal(OrderId, command.OrderId);
    }

    [Fact]
    public void Reserved_stock_moves_to_payment_with_the_priced_total()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.AwaitingInventory, total: null), Reserved(298m));

        Assert.Equal(OrderStatus.AwaitingPayment, decision.Status);
        Assert.Equal(298m, decision.Total);
        var payment = Assert.IsType<ProcessPayment>(Assert.Single(decision.Outgoing));
        Assert.Equal(298m, payment.Amount);
        Assert.Equal("cust-42", payment.CustomerId);
    }

    [Fact]
    public void Successful_payment_confirms_the_order()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.AwaitingPayment), new PaymentSucceeded(OrderId, PaymentId, 298m));

        Assert.Equal(OrderStatus.Confirmed, decision.Status);
        Assert.Equal(PaymentId, decision.PaymentId);
        Assert.IsType<OrderConfirmed>(Assert.Single(decision.Outgoing));
        Assert.Equal(TimelineKind.Success, decision.Kind);
    }

    [Fact]
    public void Rejected_stock_cancels_without_compensation()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.AwaitingInventory), new InventoryRejected(OrderId, "GPU-LTD is sold out"));

        Assert.Equal(OrderStatus.Cancelled, decision.Status);
        Assert.Equal("GPU-LTD is sold out", decision.CancellationReason);
        Assert.IsType<OrderCancelled>(Assert.Single(decision.Outgoing));
        Assert.DoesNotContain(decision.Outgoing, m => m is ReleaseInventory);
    }

    [Fact]
    public void Failed_payment_compensates_by_releasing_stock()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.AwaitingPayment), new PaymentFailed(OrderId, "Card declined"));

        Assert.Equal(OrderStatus.Compensating, decision.Status);
        Assert.IsType<ReleaseInventory>(Assert.Single(decision.Outgoing));
        Assert.Contains("Card declined", decision.CancellationReason);
    }

    [Fact]
    public void Released_stock_completes_compensation_and_keeps_the_original_reason()
    {
        var decision = OrderSaga.Decide(
            Order(OrderStatus.Compensating, reason: "Payment failed: Card declined"),
            new InventoryReleased(OrderId, StockReturned: true));

        Assert.Equal(OrderStatus.Cancelled, decision.Status);
        var cancelled = Assert.IsType<OrderCancelled>(Assert.Single(decision.Outgoing));
        Assert.Equal("Payment failed: Card declined", cancelled.Reason);
    }

    [Theory]
    [InlineData(OrderStatus.AwaitingInventory)]
    [InlineData(OrderStatus.AwaitingPayment)]
    public void Timeout_while_waiting_compensates(OrderStatus waitingIn)
    {
        var decision = OrderSaga.Decide(Order(waitingIn), new SagaTimedOut(OrderId, TimeSpan.FromSeconds(15)));

        Assert.Equal(OrderStatus.Compensating, decision.Status);
        Assert.IsType<ReleaseInventory>(Assert.Single(decision.Outgoing));
        Assert.Contains("15s", decision.Narrative);
    }

    [Theory]
    [InlineData(OrderStatus.Compensating)]
    [InlineData(OrderStatus.Cancelled)]
    public void Late_payment_after_cancellation_is_refunded(OrderStatus status)
    {
        var decision = OrderSaga.Decide(Order(status), new PaymentSucceeded(OrderId, PaymentId, 298m));

        Assert.Equal(status, decision.Status);
        Assert.IsType<RefundPayment>(Assert.Single(decision.Outgoing));
        Assert.Equal(TimelineKind.Refund, decision.Kind);
    }

    [Fact]
    public void Late_reservation_after_cancellation_is_released()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.Cancelled), Reserved());

        Assert.Equal(OrderStatus.Cancelled, decision.Status);
        Assert.IsType<ReleaseInventory>(Assert.Single(decision.Outgoing));
    }

    [Fact]
    public void Duplicate_reservation_while_awaiting_payment_is_ignored()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.AwaitingPayment), Reserved());

        Assert.Equal(OrderStatus.AwaitingPayment, decision.Status);
        Assert.Empty(decision.Outgoing);
        Assert.Equal(TimelineKind.Ignored, decision.Kind);
    }

    [Fact]
    public void Confirmed_orders_ignore_everything_including_timeouts()
    {
        object[] triggers =
        [
            new PaymentFailed(OrderId, "late"),
            new InventoryReleased(OrderId, false),
            new SagaTimedOut(OrderId, TimeSpan.FromSeconds(15)),
            new PaymentSucceeded(OrderId, PaymentId, 298m),
        ];

        foreach (var trigger in triggers)
        {
            var decision = OrderSaga.Decide(Order(OrderStatus.Confirmed), trigger);
            Assert.Equal(OrderStatus.Confirmed, decision.Status);
            Assert.Empty(decision.Outgoing);
        }
    }

    [Fact]
    public void Refund_confirmation_is_recorded_without_changing_state()
    {
        var decision = OrderSaga.Decide(Order(OrderStatus.Cancelled), new PaymentRefunded(OrderId, PaymentId, 298m));

        Assert.Equal(OrderStatus.Cancelled, decision.Status);
        Assert.Empty(decision.Outgoing);
        Assert.Equal(TimelineKind.Refund, decision.Kind);
    }

    [Fact]
    public void Every_outgoing_message_belongs_to_the_same_order()
    {
        var statuses = Enum.GetValues<OrderStatus>();
        object[] triggers =
        [
            Reserved(),
            new InventoryRejected(OrderId, "x"),
            new InventoryReleased(OrderId, true),
            new PaymentSucceeded(OrderId, PaymentId, 1m),
            new PaymentFailed(OrderId, "x"),
            new PaymentRefunded(OrderId, PaymentId, 1m),
            new SagaTimedOut(OrderId, TimeSpan.FromSeconds(1)),
        ];

        foreach (var status in statuses)
        {
            foreach (var trigger in triggers)
            {
                var decision = OrderSaga.Decide(Order(status), trigger);
                Assert.All(decision.Outgoing, m => Assert.Equal(OrderId, m.OrderId));
                Assert.False(string.IsNullOrWhiteSpace(decision.Narrative));
            }
        }
    }

    [Fact]
    public void Terminal_states_are_confirmed_and_cancelled_only()
    {
        Assert.True(OrderSaga.IsTerminal(OrderStatus.Confirmed));
        Assert.True(OrderSaga.IsTerminal(OrderStatus.Cancelled));
        Assert.False(OrderSaga.IsTerminal(OrderStatus.Compensating));
        Assert.False(OrderSaga.IsTerminal(OrderStatus.AwaitingPayment));
    }
}
