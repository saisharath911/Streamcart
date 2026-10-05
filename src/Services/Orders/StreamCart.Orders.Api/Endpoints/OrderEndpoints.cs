using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Orders.Api.Data;
using StreamCart.Orders.Api.Domain;
using StreamCart.Orders.Api.Realtime;

namespace StreamCart.Orders.Api.Endpoints;

public sealed record PlaceOrderRequest(string CustomerId, IReadOnlyList<PlaceOrderLine> Items);

public sealed record PlaceOrderLine(string Sku, int Quantity);

public sealed record OrderStatsDto(
    IReadOnlyDictionary<OrderStatus, int> ByStatus,
    int Total,
    double? AverageConfirmSeconds,
    double? P95ConfirmSeconds);

public static class OrderEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxLines = 20;
    private const int MaxQuantity = 10;

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/", PlaceOrderAsync)
            .WithSummary("Place an order (requires an Idempotency-Key header)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListOrdersAsync).WithSummary("Most recent orders");
        group.MapGet("/stats", GetStatsAsync).WithSummary("Saga outcome statistics");
        group.MapGet("/{id:guid}", GetOrderAsync).WithSummary("Order with its full saga timeline");

        return app;
    }

    private static async Task<IResult> PlaceOrderAsync(
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        PlaceOrderRequest request,
        OrdersDbContext db,
        IOutbox outbox,
        IOrderNotifier notifier,
        TimeProvider clock,
        HttpResponse response,
        CancellationToken ct)
    {
        var errors = Validate(idempotencyKey, request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        // Merge duplicate SKUs so "2 x A" and "1 x A + 1 x A" are the same request.
        var lines = request.Items
            .GroupBy(i => i.Sku.Trim().ToUpperInvariant())
            .Select(g => new OrderLineContract(g.Key, g.Sum(i => i.Quantity)))
            .OrderBy(l => l.Sku, StringComparer.Ordinal)
            .ToList();
        var customerId = request.CustomerId.Trim();
        var requestHash = Hash(customerId, lines);

        var replay = await TryReplayAsync(db, idempotencyKey!, requestHash, response, ct);
        if (replay is not null)
        {
            return replay;
        }

        var now = clock.GetUtcNow();
        var order = Order.Place(Guid.CreateVersion7(), customerId, lines, now);
        var start = OrderSaga.Start(order.Id, lines);
        var entry = order.Apply(start, "PlaceOrder", now);
        foreach (var message in start.Outgoing)
        {
            outbox.Enqueue(message);
        }

        db.Orders.Add(order);
        db.IdempotencyRecords.Add(new IdempotencyRecord(idempotencyKey!, requestHash, order.Id, now));

        try
        {
            // Order, timeline, idempotency key and the ReserveInventory command commit atomically.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent request using the same key: replay the winner.
            db.ChangeTracker.Clear();
            return await TryReplayAsync(db, idempotencyKey!, requestHash, response, ct)
                ?? throw new InvalidOperationException("Order could not be saved.");
        }

        await notifier.OrderChangedAsync(order.ToSummary(), entry.ToDto(), ct);
        return Results.Created($"/api/orders/{order.Id}", order.ToDetails());
    }

    private static async Task<IResult?> TryReplayAsync(
        OrdersDbContext db, string key, string requestHash, HttpResponse response, CancellationToken ct)
    {
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Key == key, ct);
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency key reuse",
                detail: "This Idempotency-Key was already used with a different request body.");
        }

        var order = await LoadDetailsAsync(db, existing.OrderId, ct);
        response.Headers["Idempotent-Replay"] = "true";
        return Results.Ok(order);
    }

    private static async Task<IResult> ListOrdersAsync(
        OrdersDbContext db, OrderStatus? status, int? take, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(Math.Clamp(take ?? 50, 1, 200))
            .Select(o => new OrderSummaryDto(o.Id, o.CustomerId, o.Status, o.Total, o.CancellationReason,
                o.CreatedAt, o.StatusChangedAt, o.Lines.Count))
            .ToListAsync(ct);

        return Results.Ok(orders);
    }

    private static async Task<IResult> GetOrderAsync(Guid id, OrdersDbContext db, CancellationToken ct)
    {
        var order = await LoadDetailsAsync(db, id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }

    private static async Task<IResult> GetStatsAsync(OrdersDbContext db, CancellationToken ct)
    {
        var counts = await db.Orders
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = Enum.GetValues<OrderStatus>().ToDictionary(
            s => s,
            s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);

        var confirmDurations = (await db.Orders
                .Where(o => o.Status == OrderStatus.Confirmed)
                .OrderByDescending(o => o.StatusChangedAt)
                .Take(500)
                .Select(o => new { o.CreatedAt, o.StatusChangedAt })
                .ToListAsync(ct))
            .Select(x => (x.StatusChangedAt - x.CreatedAt).TotalSeconds)
            .Order()
            .ToList();

        double? p95 = confirmDurations.Count == 0
            ? null
            : confirmDurations[Math.Min(confirmDurations.Count - 1, (int)Math.Ceiling(confirmDurations.Count * 0.95) - 1)];

        return Results.Ok(new OrderStatsDto(
            byStatus,
            byStatus.Values.Sum(),
            confirmDurations.Count == 0 ? null : confirmDurations.Average(),
            p95));
    }

    private static async Task<OrderDetailsDto?> LoadDetailsAsync(OrdersDbContext db, Guid id, CancellationToken ct)
    {
        var order = await db.Orders
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Lines)
            .Include(o => o.Timeline)
            .SingleOrDefaultAsync(o => o.Id == id, ct);
        return order?.ToDetails();
    }

    private static Dictionary<string, string[]> Validate(string? idempotencyKey, PlaceOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            errors[IdempotencyHeader] = ["A unique Idempotency-Key header (max 100 chars) is required."];
        }

        if (string.IsNullOrWhiteSpace(request.CustomerId) || request.CustomerId.Length > 64)
        {
            errors[nameof(request.CustomerId)] = ["CustomerId is required (max 64 chars)."];
        }

        if (request.Items is null || request.Items.Count is 0 or > MaxLines)
        {
            errors[nameof(request.Items)] = [$"Between 1 and {MaxLines} items are required."];
        }
        else if (request.Items.Any(i => string.IsNullOrWhiteSpace(i.Sku) || i.Quantity is < 1 or > MaxQuantity))
        {
            errors[nameof(request.Items)] = [$"Each item needs a SKU and a quantity between 1 and {MaxQuantity}."];
        }

        return errors;
    }

    private static string Hash(string customerId, IEnumerable<OrderLineContract> lines)
    {
        var canonical = customerId + "|" + string.Join(';', lines.Select(l => $"{l.Sku}x{l.Quantity}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
