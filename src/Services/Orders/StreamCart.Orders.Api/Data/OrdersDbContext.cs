using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Orders.Api.Domain;

namespace StreamCart.Orders.Api.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : MessagingDbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerId).HasMaxLength(64).IsRequired();
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(o => o.Total).HasPrecision(12, 2);
            b.Property(o => o.CancellationReason).HasMaxLength(300);
            b.Property(o => o.Version).IsRowVersion();

            b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(o => o.Timeline).WithOne().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(o => o.CreatedAt);
            // Lets the timeout watcher find stuck sagas without a full scan.
            b.HasIndex(o => new { o.Status, o.StatusChangedAt });
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines");
            b.HasKey(l => l.Id);
            b.Property(l => l.Sku).HasMaxLength(32).IsRequired();
            b.Property(l => l.UnitPrice).HasPrecision(12, 2);
        });

        modelBuilder.Entity<TimelineEntry>(b =>
        {
            b.ToTable("order_timeline");
            b.HasKey(t => t.Id);
            b.Property(t => t.Trigger).HasMaxLength(64);
            b.Property(t => t.Narrative).HasMaxLength(500);
            b.Property(t => t.Kind).HasConversion<string>().HasMaxLength(32);
            b.Property(t => t.StatusAfter).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(t => new { t.OrderId, t.At });
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_keys");
            b.HasKey(r => r.Key);
            b.Property(r => r.Key).HasMaxLength(100);
            b.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        });
    }
}
