using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Inventory.Api.Data;
using StreamCart.Inventory.Api.Domain;

namespace StreamCart.Inventory.Api.Handlers;

internal sealed class ReserveInventoryHandler(
    InventoryDbContext db,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<ReserveInventoryHandler> logger) : MessageHandler<ReserveInventory>
{
    protected override async Task HandleAsync(ReserveInventory command, MessageContext context, CancellationToken ct)
    {
        var existing = await db.Reservations.Include(r => r.Lines)
            .SingleOrDefaultAsync(r => r.OrderId == command.OrderId, ct);

        if (existing is not null)
        {
            // Idempotent replay: answer the same way we did the first time.
            outbox.Enqueue(existing.Status switch
            {
                ReservationStatus.Reserved => new InventoryReserved(
                    existing.OrderId,
                    existing.Lines.Select(l => new PricedLineContract(l.Sku, l.Quantity, l.UnitPrice)).ToList(),
                    existing.Total),
                ReservationStatus.Released => new InventoryRejected(existing.OrderId, "Order was already cancelled"),
                _ => new InventoryRejected(existing.OrderId, existing.Reason ?? "Rejected"),
            });
            logger.LogInformation("Reservation for {OrderId} already {Status}; re-sent the original answer.",
                command.OrderId, existing.Status);
            return;
        }

        var skus = command.Lines.Select(l => l.Sku).Distinct().ToList();
        var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);
        var now = clock.GetUtcNow();

        var result = StockAllocator.TryAllocate(
            products.ToDictionary(p => p.Key, p => p.Value.ToStockLevel()),
            command.Lines);

        switch (result)
        {
            case Allocated allocated:
                foreach (var line in allocated.Lines)
                {
                    products[line.Sku].Reserve(line.Quantity);
                }

                db.Reservations.Add(Reservation.Reserved(
                    command.OrderId,
                    allocated.Lines.Select(l => new ReservationLine(l.Sku, l.Quantity, l.UnitPrice)),
                    now));
                outbox.Enqueue(new InventoryReserved(command.OrderId, allocated.Lines, allocated.Total));
                logger.LogInformation("Reserved {Count} line(s) for {OrderId}, total {Total}.",
                    allocated.Lines.Count, command.OrderId, allocated.Total);
                break;

            case Rejected rejected:
                db.Reservations.Add(Reservation.RejectedFor(command.OrderId, rejected.Reason, now));
                outbox.Enqueue(new InventoryRejected(command.OrderId, rejected.Reason));
                logger.LogInformation("Rejected reservation for {OrderId}: {Reason}", command.OrderId, rejected.Reason);
                break;
        }
    }
}

internal sealed class ReleaseInventoryHandler(
    InventoryDbContext db,
    IOutbox outbox,
    TimeProvider clock,
    ILogger<ReleaseInventoryHandler> logger) : MessageHandler<ReleaseInventory>
{
    protected override async Task HandleAsync(ReleaseInventory command, MessageContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var reservation = await db.Reservations.Include(r => r.Lines)
            .SingleOrDefaultAsync(r => r.OrderId == command.OrderId, ct);

        if (reservation is null)
        {
            // Release overtook Reserve (or Reserve never arrived): leave a tombstone so a
            // late Reserve is refused rather than silently holding stock forever.
            db.Reservations.Add(Reservation.Tombstone(command.OrderId, command.Reason, now));
            outbox.Enqueue(new InventoryReleased(command.OrderId, StockReturned: false));
            logger.LogInformation("No reservation for {OrderId}; tombstone written.", command.OrderId);
            return;
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            outbox.Enqueue(new InventoryReleased(command.OrderId, StockReturned: false));
            return;
        }

        var skus = reservation.Lines.Select(l => l.Sku).ToList();
        var products = await db.Products.Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, ct);
        foreach (var line in reservation.Lines)
        {
            if (products.TryGetValue(line.Sku, out var product))
            {
                product.Release(line.Quantity);
            }
        }

        reservation.MarkReleased(command.Reason, now);
        outbox.Enqueue(new InventoryReleased(command.OrderId, StockReturned: true));
        logger.LogInformation("Released stock for {OrderId}: {Reason}", command.OrderId, command.Reason);
    }
}
