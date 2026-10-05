using StreamCart.BuildingBlocks.Contracts;
using StreamCart.Inventory.Api.Domain;
using StreamCart.Orders.Api.Domain;
using StreamCart.Payments.Api.Domain;

namespace StreamCart.UnitTests;

public class StockAllocatorTests
{
    private static readonly IReadOnlyDictionary<string, StockLevel> Stock = new Dictionary<string, StockLevel>
    {
        ["KB-75"] = new("KB-75", 149m, 10),
        ["GPU-LTD"] = new("GPU-LTD", 1899m, 1),
        ["MS-ERGO"] = new("MS-ERGO", 69m, 0),
    };

    [Fact]
    public void Allocates_and_prices_every_line()
    {
        var result = StockAllocator.TryAllocate(Stock, [new("KB-75", 2), new("GPU-LTD", 1)]);

        var allocated = Assert.IsType<Allocated>(result);
        Assert.Equal(2, allocated.Lines.Count);
        Assert.Equal((2 * 149m) + 1899m, allocated.Total);
    }

    [Fact]
    public void Rejects_the_whole_order_when_one_line_is_short()
    {
        var result = StockAllocator.TryAllocate(Stock, [new("KB-75", 1), new("GPU-LTD", 2)]);

        var rejected = Assert.IsType<Rejected>(result);
        Assert.Contains("Only 1 x GPU-LTD left", rejected.Reason);
    }

    [Fact]
    public void Reports_sold_out_and_unknown_skus()
    {
        Assert.Contains("sold out", Assert.IsType<Rejected>(StockAllocator.TryAllocate(Stock, [new("MS-ERGO", 1)])).Reason);
        Assert.Contains("Unknown SKU", Assert.IsType<Rejected>(StockAllocator.TryAllocate(Stock, [new("NOPE", 1)])).Reason);
    }

    [Fact]
    public void Rejects_empty_orders_and_non_positive_quantities()
    {
        Assert.IsType<Rejected>(StockAllocator.TryAllocate(Stock, []));
        Assert.IsType<Rejected>(StockAllocator.TryAllocate(Stock, [new("KB-75", 0)]));
    }

    [Fact]
    public void Product_reserve_and_release_round_trip()
    {
        var product = new Product("KB-75", "Keyboard", "Peripherals", 149m, 5);

        product.Reserve(3);
        Assert.Equal(2, product.Available);
        Assert.Equal(3, product.Reserved);

        product.Release(3);
        Assert.Equal(5, product.Available);
        Assert.Equal(0, product.Reserved);

        Assert.Throws<InvalidOperationException>(() => product.Reserve(6));
    }
}

public class PaymentDeciderTests
{
    [Fact]
    public void Approves_normal_payments_when_calm()
    {
        Assert.True(PaymentDecider.Decide(298m, ChaosConfig.Calm, roll: 0.0).Approved);
    }

    [Fact]
    public void Declines_over_the_single_transaction_limit_regardless_of_chaos()
    {
        var decision = PaymentDecider.Decide(5_000.01m, ChaosConfig.Calm, roll: 0.99);

        Assert.False(decision.Approved);
        Assert.Contains("limit", decision.DeclineReason);
    }

    [Theory]
    [InlineData(0.10, false)]
    [InlineData(0.34, false)]
    [InlineData(0.35, true)]
    [InlineData(0.90, true)]
    public void Chaos_failure_rate_is_applied_against_the_roll(double roll, bool approved)
    {
        var chaos = new ChaosConfig(FailureRate: 0.35, LatencyMs: 0, DuplicateDeliveryRate: 0);

        Assert.Equal(approved, PaymentDecider.Decide(100m, chaos, roll).Approved);
    }

    [Fact]
    public void Chaos_settings_are_clamped_to_safe_ranges()
    {
        var normalized = new ChaosConfig(FailureRate: 4, LatencyMs: 999_999, DuplicateDeliveryRate: -1).Normalize();

        Assert.Equal(1, normalized.FailureRate);
        Assert.Equal(ChaosConfig.MaxLatencyMs, normalized.LatencyMs);
        Assert.Equal(0, normalized.DuplicateDeliveryRate);
    }

    [Fact]
    public void Slow_gateway_preset_exceeds_the_default_saga_step_timeout()
    {
        // The demo relies on this: latency > 15s timeout => compensation, then a refund.
        Assert.True(ChaosConfig.Presets["slow-gateway"].LatencyMs > 15_000);
    }

    [Fact]
    public void Refund_only_applies_to_captured_payments()
    {
        var now = DateTimeOffset.UnixEpoch;
        var captured = Payment.Create(Guid.NewGuid(), "c", 10m, PaymentDecision.Approve, now);
        var declined = Payment.Create(Guid.NewGuid(), "c", 10m, PaymentDecision.Decline("no"), now);

        Assert.True(captured.TryRefund("late", now));
        Assert.False(captured.TryRefund("again", now));
        Assert.False(declined.TryRefund("late", now));
        Assert.Equal(PaymentStatus.Refunded, captured.Status);
    }
}

public class OrderAggregateTests
{
    [Fact]
    public void Applying_decisions_tracks_status_total_and_timeline()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var lines = new List<OrderLineContract> { new("KB-75", 2) };
        var order = Order.Place(Guid.NewGuid(), "cust-1", lines, t0);

        order.Apply(OrderSaga.Start(order.Id, lines), "PlaceOrder", t0);
        var reserved = new InventoryReserved(order.Id, [new PricedLineContract("KB-75", 2, 149m)], 298m);
        order.ApplyPricing(reserved.Lines);
        var entry = order.Apply(OrderSaga.Decide(order.ToSnapshot(), reserved), nameof(InventoryReserved), t0.AddSeconds(1));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal(298m, order.Total);
        Assert.Equal(149m, Assert.Single(order.Lines).UnitPrice);
        Assert.Equal(2, order.Timeline.Count);
        Assert.Equal(t0.AddSeconds(1), order.StatusChangedAt);
        Assert.Equal(new[] { "ProcessPayment" }, entry.Sent);
    }

    [Fact]
    public void Ignored_messages_do_not_move_the_status_clock()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var order = Order.Place(Guid.NewGuid(), "cust-1", [new OrderLineContract("KB-75", 1)], t0);

        order.Apply(OrderSaga.Decide(order.ToSnapshot(), new PaymentFailed(order.Id, "stale")), nameof(PaymentFailed), t0.AddMinutes(5));

        Assert.Equal(OrderStatus.AwaitingInventory, order.Status);
        Assert.Equal(t0, order.StatusChangedAt);
        Assert.Equal(TimelineKind.Ignored, Assert.Single(order.Timeline).Kind);
    }
}
