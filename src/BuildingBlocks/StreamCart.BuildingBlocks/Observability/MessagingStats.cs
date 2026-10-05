namespace StreamCart.BuildingBlocks.Observability;

/// <summary>
/// In-process counters surfaced on <c>/api/{service}/messaging/stats</c> so the Saga Lab dashboard can
/// show reliability behaviour (duplicates suppressed, retries) without a metrics backend.
/// Production dashboards should use the OpenTelemetry metrics instead.
/// </summary>
public sealed class MessagingStats
{
    private long _handled;
    private long _duplicatesSuppressed;
    private long _handlerFailures;
    private long _published;
    private long _publishFailures;
    private long _acksSkippedByChaos;

    public void Handled() => Interlocked.Increment(ref _handled);
    public void DuplicateSuppressed() => Interlocked.Increment(ref _duplicatesSuppressed);
    public void HandlerFailed() => Interlocked.Increment(ref _handlerFailures);
    public void Published(int count) => Interlocked.Add(ref _published, count);
    public void PublishFailed() => Interlocked.Increment(ref _publishFailures);
    public void AckSkipped() => Interlocked.Increment(ref _acksSkippedByChaos);

    public MessagingStatsSnapshot Snapshot(string service) => new(
        service,
        Interlocked.Read(ref _handled),
        Interlocked.Read(ref _duplicatesSuppressed),
        Interlocked.Read(ref _handlerFailures),
        Interlocked.Read(ref _published),
        Interlocked.Read(ref _publishFailures),
        Interlocked.Read(ref _acksSkippedByChaos));
}

public sealed record MessagingStatsSnapshot(
    string Service,
    long Handled,
    long DuplicatesSuppressed,
    long HandlerFailures,
    long Published,
    long PublishFailures,
    long AcksSkippedByChaos);
