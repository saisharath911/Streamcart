using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.Orders.Api.Data;
using StreamCart.Orders.Api.Domain;
using StreamCart.Orders.Api.Realtime;

namespace StreamCart.Orders.Api.Saga;

public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    /// <summary>How long a step may wait for a reply before the saga compensates.</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// A saga that waits forever is a resource leak. This watcher finds orders stuck in a
/// waiting state past <see cref="SagaOptions.StepTimeout"/> and feeds them a
/// <see cref="SagaTimedOut"/> trigger. Optimistic concurrency (xmin) guarantees that if the
/// real reply lands at the same moment, only one of the two transitions wins.
/// </summary>
internal sealed class SagaTimeoutWatcher(
    IServiceScopeFactory scopeFactory,
    IOrderNotifier notifier,
    TimeProvider clock,
    IOptions<SagaOptions> options,
    ILogger<SagaTimeoutWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.ScanInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanAsync(settings.StepTimeout, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Saga timeout scan failed.");
            }
        }
    }

    private async Task ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - timeout;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        var stuckIds = await db.Orders
            .Where(o => (o.Status == OrderStatus.AwaitingInventory || o.Status == OrderStatus.AwaitingPayment)
                        && o.StatusChangedAt < cutoff)
            .OrderBy(o => o.StatusChangedAt)
            .Select(o => o.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var id in stuckIds)
        {
            await using var orderScope = scopeFactory.CreateAsyncScope();
            var orderDb = orderScope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            var outbox = orderScope.ServiceProvider.GetRequiredService<IOutbox>();

            var order = await orderDb.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == id, ct);
            var decision = OrderSaga.Decide(order.ToSnapshot(), new SagaTimedOut(id, timeout));
            var entry = order.Apply(decision, nameof(SagaTimedOut), clock.GetUtcNow());
            foreach (var message in decision.Outgoing)
            {
                outbox.Enqueue(message);
            }

            try
            {
                // Order update + outbox rows commit atomically in one SaveChanges.
                await orderDb.SaveChangesAsync(ct);
                logger.LogWarning("Order {OrderId} timed out; {Narrative}", id, decision.Narrative);
                await notifier.OrderChangedAsync(order.ToSummary(), entry.ToDto(), ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Order {OrderId} changed while timing out; the real reply won.", id);
            }
        }
    }
}
