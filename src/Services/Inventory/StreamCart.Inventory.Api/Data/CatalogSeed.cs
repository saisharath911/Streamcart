using Microsoft.EntityFrameworkCore;
using StreamCart.Inventory.Api.Domain;

namespace StreamCart.Inventory.Api.Data;

/// <summary>A small developer-gear catalog. One deliberately scarce SKU makes stock-outs easy to demo.</summary>
internal static class CatalogSeed
{
    public static async Task SeedAsync(InventoryDbContext db, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct))
        {
            return;
        }

        db.Products.AddRange(
            new Product("KB-75", "75% Mechanical Keyboard", "Peripherals", 149.00m, 120),
            new Product("MS-ERGO", "Vertical Ergonomic Mouse", "Peripherals", 69.00m, 200),
            new Product("MON-27Q", "27\" QHD Monitor", "Displays", 329.00m, 60),
            new Product("DOCK-TB4", "Thunderbolt 4 Dock", "Accessories", 249.00m, 80),
            new Product("HP-ANC", "Noise-Cancelling Headphones", "Audio", 279.00m, 90),
            new Product("CHAIR-PRO", "Ergonomic Desk Chair", "Furniture", 599.00m, 25),
            new Product("DESK-LIFT", "Standing Desk Frame", "Furniture", 449.00m, 30),
            new Product("GPU-LTD", "Limited Edition GPU", "Hardware", 1899.00m, 3));

        await db.SaveChangesAsync(ct);
    }
}
