using StreamCart.BuildingBlocks.Contracts;

namespace StreamCart.Inventory.Api.Domain;

public sealed record StockLevel(string Sku, decimal UnitPrice, int Available);

public abstract record AllocationResult;

public sealed record Allocated(IReadOnlyList<PricedLineContract> Lines, decimal Total) : AllocationResult;

public sealed record Rejected(string Reason) : AllocationResult;

/// <summary>
/// All-or-nothing allocation: either every line can be satisfied and is priced, or the
/// whole reservation is rejected with the first reason found. Pure, so it is unit tested
/// without a database.
/// </summary>
public static class StockAllocator
{
    public static AllocationResult TryAllocate(
        IReadOnlyDictionary<string, StockLevel> stock,
        IReadOnlyList<OrderLineContract> lines)
    {
        if (lines.Count == 0)
        {
            return new Rejected("Order has no lines");
        }

        var priced = new List<PricedLineContract>(lines.Count);
        foreach (var line in lines)
        {
            if (!stock.TryGetValue(line.Sku, out var level))
            {
                return new Rejected($"Unknown SKU {line.Sku}");
            }

            if (line.Quantity <= 0)
            {
                return new Rejected($"Invalid quantity for {line.Sku}");
            }

            if (level.Available < line.Quantity)
            {
                return new Rejected(level.Available == 0
                    ? $"{line.Sku} is sold out"
                    : $"Only {level.Available} x {line.Sku} left, {line.Quantity} requested");
            }

            priced.Add(new PricedLineContract(line.Sku, line.Quantity, level.UnitPrice));
        }

        return new Allocated(priced, priced.Sum(p => p.UnitPrice * p.Quantity));
    }
}
