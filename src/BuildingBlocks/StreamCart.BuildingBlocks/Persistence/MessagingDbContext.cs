using Microsoft.EntityFrameworkCore;

namespace StreamCart.BuildingBlocks.Persistence;

/// <summary>
/// Base DbContext that adds the transactional outbox and inbox tables.
/// Each service owns its own database (database-per-service) and derives from this.
/// </summary>
public abstract class MessagingDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Type).HasColumnName("type").HasMaxLength(128).IsRequired();
            b.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            b.Property(x => x.PublishedAt).HasColumnName("published_at");
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);

            // Partial index: the publisher only ever scans unpublished rows.
            b.HasIndex(x => x.OccurredAt)
                .HasDatabaseName("ix_outbox_unpublished")
                .HasFilter("published_at IS NULL");
        });

        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.ToTable("inbox_processed_messages");
            // Composite key doubles as the de-duplication guard: a concurrent duplicate
            // delivery fails on insert, rolls back, and is skipped on redelivery.
            b.HasKey(x => new { x.MessageId, x.Consumer });
            b.Property(x => x.MessageId).HasColumnName("message_id");
            b.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(64);
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });
    }
}

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        Type = string.Empty;
        Envelope = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; }
    public string Envelope { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage From(Messaging.MessageEnvelope envelope) => new()
    {
        Id = envelope.MessageId,
        Type = envelope.Type,
        Envelope = envelope.ToJson(),
        OccurredAt = envelope.OccurredAt,
    };

    public void MarkPublished(DateTimeOffset at)
    {
        PublishedAt = at;
        Attempts++;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;
    }
}

public sealed class ProcessedMessage(Guid messageId, string consumer, DateTimeOffset processedAt)
{
    public Guid MessageId { get; private set; } = messageId;
    public string Consumer { get; private set; } = consumer;
    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
