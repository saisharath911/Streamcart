using StreamCart.BuildingBlocks.Contracts;
using StreamCart.BuildingBlocks.Hosting;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Persistence;
using StreamCart.Inventory.Api.Data;
using StreamCart.Inventory.Api.Endpoints;
using StreamCart.Inventory.Api.Handlers;

const string serviceName = "inventory";

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(serviceName);
builder.Services.AddOpenApi();

builder.Services.AddStreamCartDatabase<InventoryDbContext>("streamcart_inventory");
builder.Services.AddStreamCartMessaging<InventoryDbContext>(builder.Configuration, serviceName);
builder.Services.AddMessageHandler<ReserveInventory, ReserveInventoryHandler>();
builder.Services.AddMessageHandler<ReleaseInventory, ReleaseInventoryHandler>();

var app = builder.Build();

app.UseServiceDefaults();
app.MapOpenApi();
app.MapDefaultEndpoints(serviceName);
app.MapInventoryEndpoints();

await app.InitializeDatabaseAsync<InventoryDbContext>(CatalogSeed.SeedAsync);
await app.RunAsync();
