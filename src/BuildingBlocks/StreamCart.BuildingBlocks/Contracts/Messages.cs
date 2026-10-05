using StreamCart.BuildingBlocks.Messaging;

namespace StreamCart.BuildingBlocks.Contracts;

// ---------------------------------------------------------------------------
// Integration contracts shared by every service.
// Commands are imperative ("do this"), events are past tense ("this happened").
// Contracts are append-only: add optional fields, never rename or remove.
// ---------------------------------------------------------------------------

public sealed record OrderLineContract(string Sku, int Quantity);

public sealed record PricedLineContract(string Sku, int Quantity, decimal UnitPrice);

// ----- Orders -> Inventory --------------------------------------------------
public sealed record ReserveInventory(Guid OrderId, IReadOnlyList<OrderLineContract> Lines) : IIntegrationMessage;

public sealed record ReleaseInventory(Guid OrderId, string Reason) : IIntegrationMessage;

// ----- Inventory -> Orders --------------------------------------------------
public sealed record InventoryReserved(Guid OrderId, IReadOnlyList<PricedLineContract> Lines, decimal Total) : IIntegrationMessage;

public sealed record InventoryRejected(Guid OrderId, string Reason) : IIntegrationMessage;

public sealed record InventoryReleased(Guid OrderId, bool StockReturned) : IIntegrationMessage;

// ----- Orders -> Payments ---------------------------------------------------
public sealed record ProcessPayment(Guid OrderId, string CustomerId, decimal Amount) : IIntegrationMessage;

public sealed record RefundPayment(Guid OrderId, string Reason) : IIntegrationMessage;

// ----- Payments -> Orders ---------------------------------------------------
public sealed record PaymentSucceeded(Guid OrderId, Guid PaymentId, decimal Amount) : IIntegrationMessage;

public sealed record PaymentFailed(Guid OrderId, string Reason) : IIntegrationMessage;

public sealed record PaymentRefunded(Guid OrderId, Guid PaymentId, decimal Amount) : IIntegrationMessage;

// ----- Orders -> anyone (public domain events) ------------------------------
public sealed record OrderConfirmed(Guid OrderId, string CustomerId, decimal Total) : IIntegrationMessage;

public sealed record OrderCancelled(Guid OrderId, string CustomerId, string Reason) : IIntegrationMessage;
