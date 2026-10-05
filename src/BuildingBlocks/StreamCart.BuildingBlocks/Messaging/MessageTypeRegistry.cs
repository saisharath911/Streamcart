using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using StreamCart.BuildingBlocks.Contracts;

namespace StreamCart.BuildingBlocks.Messaging;

/// <summary>
/// Maps wire names ("InventoryReserved") to CLR types and back.
/// The wire name is deliberately the short type name, not the assembly-qualified
/// name, so services can be refactored without breaking the contract.
/// </summary>
public static class MessageTypeRegistry
{
    private static readonly FrozenDictionary<string, Type> TypesByName =
        typeof(ReserveInventory).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationMessage).IsAssignableFrom(t))
            .ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<string> KnownTypes => TypesByName.Keys;

    public static string NameOf<T>() where T : IIntegrationMessage => NameOf(typeof(T));

    public static string NameOf(Type type) =>
        TypesByName.ContainsKey(type.Name)
            ? type.Name
            : throw new InvalidOperationException($"{type.FullName} is not a registered integration message.");

    public static bool TryResolve(string name, [NotNullWhen(true)] out Type? type) =>
        TypesByName.TryGetValue(name, out type);

    public static IIntegrationMessage Deserialize(MessageEnvelope envelope)
    {
        if (!TryResolve(envelope.Type, out var type))
        {
            throw new InvalidOperationException($"Unknown message type '{envelope.Type}'.");
        }

        return (IIntegrationMessage)(JsonSerializer.Deserialize(envelope.Payload, type, MessageEnvelope.SerializerOptions)
            ?? throw new InvalidOperationException($"Payload for '{envelope.Type}' deserialized to null."));
    }
}
