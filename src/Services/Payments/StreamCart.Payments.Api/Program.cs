using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Hosting;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Payments.Api.Chaos;
using StreamCart.Payments.Api.Data;
using StreamCart.Payments.Api.Endpoints;
using StreamCart.Payments.Api.Handlers;

const string serviceName = "payments";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(serviceName);
builder.Services.AddOpenApi();

builder.Services.AddSingleton<ChaosState>();
// Registered before messaging so it replaces the default no-op chaos hook.
builder.Services.AddSingleton<IDeliveryChaos, PaymentsDeliveryChaos>();

builder.Services.AddStreamCartDatabase<PaymentsDbContext>("streamcart_payments");
builder.Services.AddStreamCartMessaging<PaymentsDbContext>(builder.Configuration, serviceName);
builder.Services.AddMessageHandler<ProcessPayment, ProcessPaymentHandler>();
builder.Services.AddMessageHandler<RefundPayment, RefundPaymentHandler>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapOpenApi();
app.MapDefaultEndpoints(serviceName);
app.MapPaymentEndpoints();

await app.InitializeDatabaseAsync<PaymentsDbContext>();
await app.RunAsync();
