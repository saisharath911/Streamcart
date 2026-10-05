namespace StreamCart.Inventory.Api.Domain;

public sealed class Product(string sku, string name, string category, decimal unitPrice, int available)
{
    public string Sku { get; private set; } = sku;
    public string Name { get; private set; } = name;
    public string Category { get; private set; } = category;
    public decimal UnitPrice { get; private set; } = unitPrice;
    public int Available { get; private set; } = available;
    public int Reserved { get; private set; }

    /// <summary>xmin-based optimistic concurrency: two orders cannot oversell the same SKU.</summary>
    public uint Version { get; private set; }

    public StockLevel ToStockLevel() => new(Sku, UnitPrice, Available);

    public void Reserve(int quantity)
    {
        if (quantity > Available)
        {
            throw new InvalidOperationException($"Cannot reserve {quantity} x {Sku}; only {Available} available.");
        }

        Available -= quantity;
        Reserved += quantity;
    }

    public void Release(int quantity)
    {
        Available += quantity;
        Reserved = Math.Max(0, Reserved - quantity);
    }

    public void Restock(int quantity) => Available += quantity;
}

public enum ReservationStatus
{
    Reserved,
    Rejected,
    Released,
}

/// <summary>
/// One row per order. Keeping rejected and released reservations (tombstones) is what makes
/// both commands idempotent and order-independent: a Release that overtakes its Reserve
/// leaves a tombstone, so the late Reserve is refused instead of leaking stock.
/// </summary>
public sealed class Reservation
{
    private Reservation()
    {
    }

    public Guid OrderId { get; private set; }
    public ReservationStatus Status { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public List<ReservationLine> Lines { get; private set; } = [];

    public static Reservation Reserved(Guid orderId, IEnumerable<ReservationLine> lines, DateTimeOffset now)
    {
        var reservation = new Reservation { OrderId = orderId, Status = ReservationStatus.Reserved, CreatedAt = now, UpdatedAt = now };
        reservation.Lines.AddRange(lines);
        return reservation;
    }

    public static Reservation RejectedFor(Guid orderId, string reason, DateTimeOffset now) =>
        new() { OrderId = orderId, Status = ReservationStatus.Rejected, Reason = reason, CreatedAt = now, UpdatedAt = now };

    public static Reservation Tombstone(Guid orderId, string reason, DateTimeOffset now) =>
        new() { OrderId = orderId, Status = ReservationStatus.Released, Reason = reason, CreatedAt = now, UpdatedAt = now };

    public void MarkReleased(string reason, DateTimeOffset now)
    {
        Status = ReservationStatus.Released;
        Reason = reason;
        UpdatedAt = now;
    }

    public decimal Total => Lines.Sum(l => l.UnitPrice * l.Quantity);
}

public sealed class ReservationLine(string sku, int quantity, decimal unitPrice)
{
    public Guid OrderId { get; private set; }
    public string Sku { get; private set; } = sku;
    public int Quantity { get; private set; } = quantity;
    public decimal UnitPrice { get; private set; } = unitPrice;
}
