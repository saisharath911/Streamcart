using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Observability;

namespace StreamCart.BuildingBlocks.Persistence;

public interface IIncomingMessageProcessor
{
    Task<ProcessingResult> ProcessAsync(MessageEnvelope envelope, int receiveCount, CancellationToken cancellationToken);
}

/// <summary>
/// Transactional inbox: checks the de-duplication table, runs the handler, records the
/// message as processed and commits any outbox messages - all in one database transaction.
/// This turns SQS's at-least-once delivery into effectively-once processing.
/// </summary>
internal sealed class IncomingMessageProcessor<TContext>(
    IServiceProvider services,
    TContext db,
    ServiceIdentity identity,
    MessagingStats stats,
    TimeProvider clock,
    ILogger<IncomingMessageProcessor<TContext>> logger) : IIncomingMessageProcessor
    where TContext : MessagingDbContext
{
    public async Task<ProcessingResult> ProcessAsync(MessageEnvelope envelope, int receiveCount, CancellationToken cancellationToken)
    {
        if (!MessageTypeRegistry.TryResolve(envelope.Type, out _))
        {
            logger.LogWarning("Ignoring message {MessageId} with unknown type {Type}.", envelope.MessageId, envelope.Type);
            return Record(envelope, ProcessingResult.UnknownType);
        }

        var handler = services.GetKeyedService<IMessageHandler>(envelope.Type);
        if (handler is null)
        {
            // Subscription filter policies should prevent this; log loudly if they drift.
            logger.LogWarning("{Service} has no handler for {Type}; check the SNS filter policy.", identity.Name, envelope.Type);
            return Record(envelope, ProcessingResult.NoHandler);
        }

        var message = MessageTypeRegistry.Deserialize(envelope);
        var strategy = db.Database.CreateExecutionStrategy();
        MessageContext context = null!;

        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            context = new MessageContext(envelope, receiveCount);

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            var alreadyProcessed = await db.ProcessedMessages.AnyAsync(
                p => p.MessageId == envelope.MessageId && p.Consumer == identity.Name, cancellationToken);
            if (alreadyProcessed)
            {
                return ProcessingResult.Duplicate;
            }

            await handler.HandleAsync(message, context, cancellationToken);

            db.ProcessedMessages.Add(new ProcessedMessage(envelope.MessageId, identity.Name, clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return ProcessingResult.Handled;
        });

        if (result == ProcessingResult.Handled)
        {
            foreach (var action in context.AfterCommitActions)
            {
                try
                {
                    await action(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Post-commit work is best effort by definition (e.g. UI notifications).
                    logger.LogWarning(ex, "Post-commit action failed for message {MessageId}.", envelope.MessageId);
                }
            }
        }

        return Record(envelope, result);
    }

    private ProcessingResult Record(MessageEnvelope envelope, ProcessingResult result)
    {
        if (result == ProcessingResult.Duplicate)
        {
            stats.DuplicateSuppressed();
            logger.LogInformation("Duplicate delivery of {Type} {MessageId} suppressed by the inbox.", envelope.Type, envelope.MessageId);
        }
        else if (result == ProcessingResult.Handled)
        {
            stats.Handled();
        }

        Telemetry.MessagesProcessed.Add(1,
            new KeyValuePair<string, object?>("type", envelope.Type),
            new KeyValuePair<string, object?>("result", result.ToString()));
        return result;
    }
}
