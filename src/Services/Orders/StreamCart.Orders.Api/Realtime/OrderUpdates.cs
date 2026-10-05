using Microsoft.AspNetCore.SignalR;
using StreamCart.Orders.Api.Domain;

namespace StreamCart.Orders.Api.Realtime;

/// <summary>Server-to-client only hub: the dashboard subscribes to <c>orderUpdated</c>.</summary>
public sealed class OrdersHub : Hub;

public sealed record OrderLineDto(string Sku, int Quantity, decimal? UnitPrice);

public sealed record TimelineEntryDto(
    DateTimeOffset At,
    string Trigger,
    string Narrative,
    TimelineKind Kind,
    OrderStatus StatusAfter,
    IReadOnlyList<string> Sent);

public sealed record OrderSummaryDto(
    Guid Id,
    string CustomerId,
    OrderStatus Status,
    decimal? Total,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    int LineCount);

public sealed record OrderDetailsDto(
    OrderSummaryDto Summary,
    IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<TimelineEntryDto> Timeline);

public sealed record OrderUpdatedMessage(OrderSummaryDto Order, TimelineEntryDto? Entry);

public static class OrderMappings
{
    public static OrderSummaryDto ToSummary(this Order o) =>
        new(o.Id, o.CustomerId, o.Status, o.Total, o.CancellationReason, o.CreatedAt, o.StatusChangedAt, o.Lines.Count);

    public static TimelineEntryDto ToDto(this TimelineEntry t) =>
        new(t.At, t.Trigger, t.Narrative, t.Kind, t.StatusAfter, t.Sent);

    public static OrderDetailsDto ToDetails(this Order o) =>
        new(o.ToSummary(),
            o.Lines.Select(l => new OrderLineDto(l.Sku, l.Quantity, l.UnitPrice)).ToList(),
            o.Timeline.OrderBy(t => t.At).ThenBy(t => t.Id).Select(t => t.ToDto()).ToList());
}

public interface IOrderNotifier
{
    Task OrderChangedAsync(OrderSummaryDto order, TimelineEntryDto? entry, CancellationToken cancellationToken);
}

internal sealed class SignalROrderNotifier(IHubContext<OrdersHub> hub) : IOrderNotifier
{
    public Task OrderChangedAsync(OrderSummaryDto order, TimelineEntryDto? entry, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync("orderUpdated", new OrderUpdatedMessage(order, entry), cancellationToken);
}
