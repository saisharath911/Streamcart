using Microsoft.EntityFrameworkCore;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Payments.Api.Domain;

namespace StreamCart.Payments.Api.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : MessagingDbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.HasKey(p => p.Id);
            // One payment per order, enforced by the database - the last line of defence
            // against double charging if every other idempotency layer failed.
            b.HasIndex(p => p.OrderId).IsUnique();
            b.Property(p => p.CustomerId).HasMaxLength(64).IsRequired();
            b.Property(p => p.Amount).HasPrecision(12, 2);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Reason).HasMaxLength(300);
            b.HasIndex(p => p.CreatedAt);
        });
    }
}
