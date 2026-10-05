using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Observability;

namespace StreamCart.BuildingBlocks.Persistence;

/// <summary>
/// Relays committed outbox rows to SNS. Uses <c>FOR UPDATE SKIP LOCKED</c> so any number of
/// service replicas can run the publisher concurrently without double-publishing a row
/// within a batch (consumers stay idempotent for the rare crash-after-publish case).
/// </summary>
internal sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopeFactory,
    IMessageTransport transport,
    MessagingStats stats,
    TimeProvider clock,
    IOptions<MessagingOptions> options,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : MessagingDbContext
{
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundServices)
        {
            logger.LogInformation("Outbox publisher disabled by configuration.");
            return;
        }

        logger.LogInformation("Outbox publisher started (batch {BatchSize}, poll {Interval}).",
            _options.OutboxBatchSize, _options.OutboxPollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publisher iteration failed; backing off.");
                published = 0;
                await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken).ConfigureAwait(false);
            }

            // A full batch means there is probably more waiting: loop immediately.
            if (published < _options.OutboxBatchSize)
            {
                await Task.Delay(_options.OutboxPollInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var batchSize = _options.OutboxBatchSize;
            var maxAttempts = _options.OutboxMaxAttempts;
            var batch = await db.OutboxMessages
                .FromSql($"""
                    SELECT * FROM outbox_messages
                    WHERE published_at IS NULL AND attempts < {maxAttempts}
                    ORDER BY occurred_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                return 0;
            }

            var publishedCount = 0;
            foreach (var row in batch)
            {
                var envelope = MessageEnvelope.FromJson(row.Envelope);
                if (envelope is null)
                {
                    row.MarkFailed("Envelope could not be deserialized.");
                    continue;
                }

                // Producer span parented to the request that enqueued the message, and
                // propagated onward so the consumer span joins the same trace.
                ActivityContext.TryParse(envelope.TraceParent, null, out var parent);
                using var activity = Telemetry.Source.StartActivity($"{envelope.Type} publish", ActivityKind.Producer, parent);
                activity?.SetTag("messaging.system", "aws_sns");
                activity?.SetTag("messaging.message.id", envelope.MessageId.ToString());
                activity?.SetTag("streamcart.order_id", envelope.CorrelationId.ToString());

                try
                {
                    await transport.PublishAsync(envelope with { TraceParent = activity?.Id ?? envelope.TraceParent }, ct);
                    row.MarkPublished(clock.GetUtcNow());
                    publishedCount++;
                    Telemetry.OutboxPublished.Add(1, new KeyValuePair<string, object?>("type", envelope.Type));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row.MarkFailed(ex.Message);
                    stats.PublishFailed();
                    Telemetry.OutboxPublishFailed.Add(1, new KeyValuePair<string, object?>("type", envelope.Type));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    logger.LogWarning(ex, "Publishing outbox message {MessageId} ({Type}) failed, attempt {Attempt}.",
                        row.Id, row.Type, row.Attempts);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            stats.Published(publishedCount);
            return batch.Count;
        });
    }
}
