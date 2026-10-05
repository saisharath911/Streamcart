using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Orders.Api.Domain;
using StreamCart.Orders.Api.Endpoints;
using StreamCart.Orders.Api.Realtime;

namespace StreamCart.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class OrderFlowTests(OrdersApiFixture app) : IClassFixture<OrdersApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly PlaceOrderRequest SampleOrder = new("cust-it", [new PlaceOrderLine("KB-75", 2)]);

    [Fact]
    public async Task Placing_an_order_publishes_ReserveInventory_through_the_outbox()
    {
        var order = await PlaceAsync(SampleOrder, Guid.NewGuid().ToString());

        Assert.Equal(OrderStatus.AwaitingInventory, order.Summary.Status);
        var envelope = await app.Transport.WaitForAsync(order.Summary.Id, nameof(ReserveInventory));
        var command = Assert.IsType<ReserveInventory>(MessageTypeRegistry.Deserialize(envelope));
        Assert.Equal("KB-75", Assert.Single(command.Lines).Sku);
    }

    [Fact]
    public async Task Same_idempotency_key_replays_the_original_order()
    {
        var key = Guid.NewGuid().ToString();
        var client = app.CreateClient();

        var first = await SendAsync(client, SampleOrder, key);
        var second = await SendAsync(client, SampleOrder, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        var a = await first.Content.ReadFromJsonAsync<OrderDetailsDto>(Json);
        var b = await second.Content.ReadFromJsonAsync<OrderDetailsDto>(Json);
        Assert.Equal(a!.Summary.Id, b!.Summary.Id);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_rejected()
    {
        var key = Guid.NewGuid().ToString();
        var client = app.CreateClient();

        await SendAsync(client, SampleOrder, key);
        var conflicting = await SendAsync(client, SampleOrder with { CustomerId = "someone-else" }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflicting.StatusCode);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_a_validation_error()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/orders", SampleOrder, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Saga_confirms_the_order_and_the_inbox_suppresses_duplicates()
    {
        var order = await PlaceAsync(SampleOrder, Guid.NewGuid().ToString());
        var id = order.Summary.Id;

        var reserved = Envelope(new InventoryReserved(id, [new PricedLineContract("KB-75", 2, 149m)], 298m));
        Assert.Equal(ProcessingResult.Handled, await DeliverAsync(reserved));
        Assert.Equal(ProcessingResult.Duplicate, await DeliverAsync(reserved));

        var payment = await app.Transport.WaitForAsync(id, nameof(ProcessPayment));
        Assert.Equal(298m, Assert.IsType<ProcessPayment>(MessageTypeRegistry.Deserialize(payment)).Amount);

        Assert.Equal(ProcessingResult.Handled, await DeliverAsync(Envelope(new PaymentSucceeded(id, Guid.NewGuid(), 298m))));

        var final = await app.CreateClient().GetFromJsonAsync<OrderDetailsDto>($"/api/orders/{id}", Json);
        Assert.Equal(OrderStatus.Confirmed, final!.Summary.Status);
        Assert.Equal(298m, final.Summary.Total);
        Assert.Equal(3, final.Timeline.Count);
        await app.Transport.WaitForAsync(id, nameof(OrderConfirmed));
    }

    [Fact]
    public async Task Failed_payment_compensates_and_cancels()
    {
        var order = await PlaceAsync(SampleOrder, Guid.NewGuid().ToString());
        var id = order.Summary.Id;

        await DeliverAsync(Envelope(new InventoryReserved(id, [new PricedLineContract("KB-75", 2, 149m)], 298m)));
        await DeliverAsync(Envelope(new PaymentFailed(id, "Card declined")));
        await app.Transport.WaitForAsync(id, nameof(ReleaseInventory));
        await DeliverAsync(Envelope(new InventoryReleased(id, StockReturned: true)));

        var final = await app.CreateClient().GetFromJsonAsync<OrderDetailsDto>($"/api/orders/{id}", Json);
        Assert.Equal(OrderStatus.Cancelled, final!.Summary.Status);
        Assert.Contains("Card declined", final.Summary.CancellationReason);
    }

    private async Task<OrderDetailsDto> PlaceAsync(PlaceOrderRequest request, string key)
    {
        var response = await SendAsync(app.CreateClient(), request, key);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDetailsDto>(Json))!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, PlaceOrderRequest request, string key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(message);
    }

    private static MessageEnvelope Envelope(IIntegrationMessage message) =>
        MessageEnvelope.Create(message, "test", TimeProvider.System);

    private async Task<ProcessingResult> DeliverAsync(MessageEnvelope envelope)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IIncomingMessageProcessor>();
        return await processor.ProcessAsync(envelope, receiveCount: 1, CancellationToken.None);
    }
}
