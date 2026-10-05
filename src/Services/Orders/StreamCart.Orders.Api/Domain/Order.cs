using StreamCart.BuildingBlocks.Contracts;

namespace StreamCart.Orders.Api.Domain;

public sealed class Order
{
    private Order()
    {
        CustomerId = string.Empty;
    }

    public Guid Id { get; private set; }
    public string CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal? Total { get; private set; }
    public Guid? PaymentId { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>Mapped to PostgreSQL's <c>xmin</c> system column for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    public List<OrderLine> Lines { get; private set; } = [];
    public List<TimelineEntry> Timeline { get; private set; } = [];

    public static Order Place(Guid id, string customerId, IEnumerable<OrderLineContract> lines, DateTimeOffset now)
    {
        var order = new Order
        {
            Id = id,
            CustomerId = customerId,
            Status = OrderStatus.AwaitingInventory,
            CreatedAt = now,
            StatusChangedAt = now,
        };
        order.Lines.AddRange(lines.Select(l => new OrderLine(l.Sku, l.Quantity)));
        return order;
    }

    public OrderSnapshot ToSnapshot() => new(Id, CustomerId, Status, Total, CancellationReason);

    /// <summary>Applies a saga decision and records it on the order's timeline.</summary>
    public TimelineEntry Apply(SagaDecision decision, string trigger, DateTimeOffset now)
    {
        if (decision.Status != Status)
        {
            Status = decision.Status;
            StatusChangedAt = now;
        }

        Total = decision.Total ?? Total;
        PaymentId = decision.PaymentId ?? PaymentId;
        CancellationReason = decision.CancellationReason ?? CancellationReason;

        var entry = new TimelineEntry(Id, now, trigger, decision.Narrative, decision.Kind, Status,
            decision.Outgoing.Select(m => m.GetType().Name).ToArray());
        Timeline.Add(entry);
        return entry;
    }

    public void ApplyPricing(IEnumerable<PricedLineContract> priced)
    {
        var prices = priced.ToDictionary(p => p.Sku, p => p.UnitPrice, StringComparer.Ordinal);
        foreach (var line in Lines)
        {
            if (prices.TryGetValue(line.Sku, out var price))
            {
                line.UnitPrice = price;
            }
        }
    }
}

public sealed class OrderLine(string sku, int quantity)
{
    public int Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = sku;
    public int Quantity { get; private set; } = quantity;
    public decimal? UnitPrice { get; set; }
}

public sealed class TimelineEntry
{
    private TimelineEntry()
    {
        Trigger = string.Empty;
        Narrative = string.Empty;
        Sent = [];
    }

    public TimelineEntry(Guid orderId, DateTimeOffset at, string trigger, string narrative, TimelineKind kind, OrderStatus statusAfter, string[] sent)
    {
        OrderId = orderId;
        At = at;
        Trigger = trigger;
        Narrative = narrative;
        Kind = kind;
        StatusAfter = statusAfter;
        Sent = sent;
    }

    public long Id { get; private set; }
    public Guid OrderId { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string Trigger { get; private set; }
    public string Narrative { get; private set; }
    public TimelineKind Kind { get; private set; }
    public OrderStatus StatusAfter { get; private set; }

    /// <summary>Message types the saga sent in response (stored as a PostgreSQL text[]).</summary>
    public string[] Sent { get; private set; }
}

public sealed class IdempotencyRecord(string key, string requestHash, Guid orderId, DateTimeOffset createdAt)
{
    public string Key { get; private set; } = key;
    public string RequestHash { get; private set; } = requestHash;
    public Guid OrderId { get; private set; } = orderId;
    public DateTimeOffset CreatedAt { get; private set; } = createdAt;
}
