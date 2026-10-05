using Microsoft.EntityFrameworkCore;
using StreamCart.Inventory.Api.Data;
using StreamCart.Inventory.Api.Domain;

namespace StreamCart.Inventory.Api.Endpoints;

public sealed record ProductDto(string Sku, string Name, string Category, decimal UnitPrice, int Available, int Reserved);

public sealed record RestockRequest(int Quantity);

public sealed record ReservationDto(Guid OrderId, ReservationStatus Status, string? Reason, decimal Total, DateTimeOffset UpdatedAt);

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Catalog");

        products.MapGet("/", async (InventoryDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Products.AsNoTracking()
                .OrderBy(p => p.Category).ThenBy(p => p.Name)
                .Select(p => new ProductDto(p.Sku, p.Name, p.Category, p.UnitPrice, p.Available, p.Reserved))
                .ToListAsync(ct)));

        products.MapPost("/{sku}/restock", async (string sku, RestockRequest request, InventoryDbContext db, CancellationToken ct) =>
        {
            if (request.Quantity is < 1 or > 1000)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Quantity)] = ["Quantity must be between 1 and 1000."],
                });
            }

            var product = await db.Products.SingleOrDefaultAsync(p => p.Sku == sku, ct);
            if (product is null)
            {
                return Results.NotFound();
            }

            product.Restock(request.Quantity);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ProductDto(product.Sku, product.Name, product.Category, product.UnitPrice, product.Available, product.Reserved));
        }).WithSummary("Add stock to a SKU");

        app.MapGet("/api/reservations", async (InventoryDbContext db, int? take, CancellationToken ct) =>
        {
            var rows = await db.Reservations.AsNoTracking()
                .Include(r => r.Lines)
                .OrderByDescending(r => r.UpdatedAt)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new ReservationDto(r.OrderId, r.Status, r.Reason, r.Total, r.UpdatedAt)));
        }).WithTags("Catalog");

        return app;
    }
}
