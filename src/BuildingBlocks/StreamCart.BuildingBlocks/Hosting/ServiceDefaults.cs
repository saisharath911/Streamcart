using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StreamCart.BuildingBlocks.Messaging;
using StreamCart.BuildingBlocks.Observability;

namespace StreamCart.BuildingBlocks.Hosting;

/// <summary>
/// Cross-cutting defaults every service gets: structured logging, OpenTelemetry tracing,
/// health checks, RFC 7807 problem details and CORS for the dashboard.
/// </summary>
public static class ServiceDefaults
{
    public const string DashboardCorsPolicy = "dashboard";

    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        if (!builder.Environment.IsDevelopment())
        {
            // One JSON object per line: CloudWatch Logs Insights can query fields directly.
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
            });
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(Telemetry.SourceName)
                    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation();

                // Exports only when an OTLP endpoint is configured (Jaeger locally, ADOT/X-Ray in AWS).
                if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                {
                    tracing.AddOtlpExporter();
                }
            });

        builder.Services.AddHealthChecks();
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173"];
        builder.Services.AddCors(o => o.AddPolicy(DashboardCorsPolicy, p => p
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders("Idempotent-Replay", "Location")));

        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors(DashboardCorsPolicy);
        return app;
    }

    public static IEndpointRouteBuilder MapDefaultEndpoints(this IEndpointRouteBuilder endpoints, string serviceName)
    {
        // Liveness: the process is up. Readiness: dependencies (database) are reachable.
        endpoints.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

        // Prefixed with the service name so one gateway (ALB / nginx) can route by path.
        endpoints.MapGet($"/api/{serviceName}/messaging/stats", (MessagingStats stats) => Results.Ok(stats.Snapshot(serviceName)))
            .WithTags("Reliability");

        endpoints.MapGet($"/api/{serviceName}/messaging/contracts", () => Results.Ok(MessageTypeRegistry.KnownTypes.Order()))
            .WithTags("Reliability");

        return endpoints;
    }
}
