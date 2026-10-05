namespace StreamCart.BuildingBlocks.Messaging;

/// <summary>Metadata about the message being handled, plus post-commit hooks.</summary>
public sealed class MessageContext(MessageEnvelope envelope, int receiveCount)
{
    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];

    public Guid MessageId => envelope.MessageId;
    public Guid CorrelationId => envelope.CorrelationId;
    public string MessageType => envelope.Type;
    public string Source => envelope.Source;
    public DateTimeOffset OccurredAt => envelope.OccurredAt;

    /// <summary>1 on first delivery; &gt; 1 when SQS redelivered the message.</summary>
    public int ReceiveCount => receiveCount;

    /// <summary>
    /// Registers work that must only run once the handler's transaction has committed
    /// (e.g. pushing a SignalR update). Never put state changes here.
    /// </summary>
    public void OnCommitted(Func<CancellationToken, Task> action) => _afterCommit.Add(action);

    internal IReadOnlyList<Func<CancellationToken, Task>> AfterCommitActions => _afterCommit;
}

/// <summary>Non-generic handler contract used by the dispatcher.</summary>
public interface IMessageHandler
{
    Task HandleAsync(IIntegrationMessage message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for strongly-typed handlers. Handlers must be deterministic with respect
/// to the database: they run inside a transaction together with the inbox record and any
/// outbox messages they enqueue, so either everything commits or nothing does.
/// </summary>
public abstract class MessageHandler<TMessage> : IMessageHandler
    where TMessage : IIntegrationMessage
{
    Task IMessageHandler.HandleAsync(IIntegrationMessage message, MessageContext context, CancellationToken cancellationToken) =>
        HandleAsync((TMessage)message, context, cancellationToken);

    protected abstract Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>Enqueues messages for reliable, transactional publication.</summary>
public interface IOutbox
{
    /// <summary>
    /// Adds the message to the current unit of work. It is persisted by the next
    /// <c>SaveChangesAsync</c> and published asynchronously by the outbox publisher.
    /// </summary>
    void Enqueue(IIntegrationMessage message);
}

/// <summary>Publishes a serialized envelope to the broker.</summary>
public interface IMessageTransport
{
    Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Fault-injection hooks used by the Chaos Lab. Both run outside the handler's database
/// transaction, so injected latency never holds locks.
/// </summary>
public interface IDeliveryChaos
{
    /// <summary>Simulates a slow downstream dependency before the message is processed.</summary>
    ValueTask BeforeProcessingAsync(MessageEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Simulates the consumer crashing after it committed but before it acknowledged,
    /// which forces SQS to redeliver and the inbox to suppress the duplicate.
    /// </summary>
    bool ShouldSkipAcknowledgement(MessageEnvelope envelope, ProcessingResult result);
}

internal sealed class NoDeliveryChaos : IDeliveryChaos
{
    public ValueTask BeforeProcessingAsync(MessageEnvelope envelope, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public bool ShouldSkipAcknowledgement(MessageEnvelope envelope, ProcessingResult result) => false;
}

public sealed record ServiceIdentity(string Name);

public enum ProcessingResult
{
    Handled,
    Duplicate,
    UnknownType,
    NoHandler,
}
