namespace StreamCart.BuildingBlocks.Messaging;

/// <summary>
/// Marker for any command or event that crosses a service boundary.
/// Every implementation must carry the <c>OrderId</c> it belongs to so that
/// consumers can correlate, partition and de-duplicate.
/// </summary>
public interface IIntegrationMessage
{
    Guid OrderId { get; }
}
