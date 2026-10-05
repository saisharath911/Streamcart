using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Inventory.Api.Domain;

namespace StreamCart.Inventory.Api.Data;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : MessagingDbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.HasKey(p => p.Sku);
            b.Property(p => p.Sku).HasMaxLength(32);
            b.Property(p => p.Name).HasMaxLength(120).IsRequired();
            b.Property(p => p.Category).HasMaxLength(60).IsRequired();
            b.Property(p => p.UnitPrice).HasPrecision(12, 2);
            b.Property(p => p.Version).IsRowVersion();
            b.ToTable(t => t.HasCheckConstraint("ck_products_available_non_negative", "\"Available\" >= 0"));
        });

        modelBuilder.Entity<Reservation>(b =>
        {
            b.ToTable("reservations");
            b.HasKey(r => r.OrderId);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(r => r.Reason).HasMaxLength(300);
            b.Ignore(r => r.Total);
            b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => r.UpdatedAt);
        });

        modelBuilder.Entity<ReservationLine>(b =>
        {
            b.ToTable("reservation_lines");
            b.HasKey(l => new { l.OrderId, l.Sku });
            b.Property(l => l.Sku).HasMaxLength(32);
            b.Property(l => l.UnitPrice).HasPrecision(12, 2);
        });
    }
}
