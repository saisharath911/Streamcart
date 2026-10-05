using Microsoft.EntityFrameworkCore;
using StreamCart.Payments.Api.Chaos;
using StreamCart.Payments.Api.Data;
using StreamCart.Payments.Api.Domain;

namespace StreamCart.Payments.Api.Endpoints;

public sealed record PaymentDto(Guid Id, Guid OrderId, decimal Amount, PaymentStatus Status, string? Reason, DateTimeOffset UpdatedAt);

public sealed record ChaosResponse(ChaosConfig Current, IReadOnlyCollection<string> Presets);

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/payments", async (PaymentsDbContext db, int? take, CancellationToken ct) =>
            Results.Ok(await db.Payments.AsNoTracking()
                .OrderByDescending(p => p.UpdatedAt)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .Select(p => new PaymentDto(p.Id, p.OrderId, p.Amount, p.Status, p.Reason, p.UpdatedAt))
                .ToListAsync(ct)))
            .WithTags("Payments");

        var chaos = app.MapGroup("/api/chaos").WithTags("Chaos Lab");

        chaos.MapGet("/", (ChaosState state) =>
            Results.Ok(new ChaosResponse(state.Current, ChaosConfig.Presets.Keys.ToList())));

        chaos.MapPut("/", (ChaosConfig config, ChaosState state, ILogger<ChaosState> logger) =>
        {
            var applied = state.Set(config);
            logger.LogWarning("Chaos configuration changed: {@Chaos}", applied);
            return Results.Ok(new ChaosResponse(applied, ChaosConfig.Presets.Keys.ToList()));
        }).WithSummary("Set failure rate, latency and duplicate-delivery rate");

        chaos.MapPost("/presets/{name}", (string name, ChaosState state, ILogger<ChaosState> logger) =>
        {
            if (!ChaosConfig.Presets.TryGetValue(name, out var preset))
            {
                return Results.NotFound();
            }

            var applied = state.Set(preset);
            logger.LogWarning("Chaos preset {Preset} applied.", name);
            return Results.Ok(new ChaosResponse(applied, ChaosConfig.Presets.Keys.ToList()));
        }).WithSummary("Apply a named chaos scenario");

        return app;
    }
}
