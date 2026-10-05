using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;

namespace StreamCart.UnitTests;

public class MessagingTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public void Envelope_round_trips_through_json()
    {
        var orderId = Guid.NewGuid();
        var original = new InventoryReserved(orderId, [new PricedLineContract("KB-75", 2, 149m)], 298m);

        var envelope = MessageEnvelope.Create(original, "inventory", Clock, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        var restored = MessageEnvelope.FromJson(envelope.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(envelope.MessageId, restored.MessageId);
        Assert.Equal("InventoryReserved", restored.Type);
        Assert.Equal(orderId, restored.CorrelationId);
        Assert.Equal(envelope.TraceParent, restored.TraceParent);

        var message = Assert.IsType<InventoryReserved>(MessageTypeRegistry.Deserialize(restored));
        Assert.Equal(298m, message.Total);
        Assert.Equal("KB-75", Assert.Single(message.Lines).Sku);
    }

    [Fact]
    public void Every_message_gets_a_unique_id()
    {
        var command = new ReleaseInventory(Guid.NewGuid(), "test");

        var a = MessageEnvelope.Create(command, "orders", Clock);
        var b = MessageEnvelope.Create(command, "orders", Clock);

        Assert.NotEqual(a.MessageId, b.MessageId);
    }

    [Fact]
    public void Registry_knows_every_contract_by_short_name()
    {
        string[] expected =
        [
            "ReserveInventory", "ReleaseInventory", "InventoryReserved", "InventoryRejected", "InventoryReleased",
            "ProcessPayment", "RefundPayment", "PaymentSucceeded", "PaymentFailed", "PaymentRefunded",
            "OrderConfirmed", "OrderCancelled",
        ];

        Assert.Equal(expected.Order(), MessageTypeRegistry.KnownTypes.Order());
        foreach (var name in expected)
        {
            Assert.True(MessageTypeRegistry.TryResolve(name, out var type));
            Assert.Equal(name, type!.Name);
        }
    }

    [Fact]
    public void Garbage_is_rejected_safely()
    {
        Assert.Null(MessageEnvelope.FromJson("{ not json"));
        Assert.False(MessageTypeRegistry.TryResolve("DropTables", out _));
        Assert.Throws<InvalidOperationException>(() =>
            MessageTypeRegistry.Deserialize(new MessageEnvelope(Guid.NewGuid(), "DropTables", Guid.Empty, "x", DateTimeOffset.UnixEpoch, "{}")));
    }
}
