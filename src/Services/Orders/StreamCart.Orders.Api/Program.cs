using System.Text.Json.Serialization;
using StreamCart.BuildingBlocks.Hosting;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Orders.Api.Data;
using StreamCart.Orders.Api.Endpoints;
using StreamCart.Orders.Api.Realtime;
using StreamCart.Orders.Api.Saga;

const string serviceName = "orders";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(serviceName);
builder.Services.AddOpenApi();

builder.Services.AddStreamCartDatabase<OrdersDbContext>("streamcart_orders");
builder.Services.AddStreamCartMessaging<OrdersDbContext>(builder.Configuration, serviceName);

builder.Services.Configure<SagaOptions>(builder.Configuration.GetSection(SagaOptions.SectionName));
builder.Services.AddOrderSaga();

builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IOrderNotifier, SignalROrderNotifier>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapOpenApi();
app.MapDefaultEndpoints(serviceName);
app.MapOrderEndpoints();
app.MapHub<OrdersHub>("/hubs/orders");

await app.InitializeDatabaseAsync<OrdersDbContext>();
await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
