using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace StreamCart.BuildingBlocks.Persistence;

public static class DatabaseExtensions
{
    /// <summary>
    /// Registers the service's PostgreSQL DbContext. The connection string comes from
    /// <c>ConnectionStrings:Default</c> locally, or from discrete <c>Database:*</c> settings in AWS,
    /// where the password is injected from Secrets Manager by ECS.
    /// </summary>
    public static IServiceCollection AddStreamCartDatabase<TContext>(
        this IServiceCollection services,
        string defaultDatabaseName)
        where TContext : MessagingDbContext
    {
        // Resolved from the final IConfiguration at first use, so environment variables and
        // test overrides always win over appsettings.json.
        services.AddDbContext<TContext>((sp, options) =>
            options.UseNpgsql(
                BuildConnectionString(sp.GetRequiredService<IConfiguration>(), defaultDatabaseName),
                npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 5)));

        services.AddHealthChecks()
            .AddCheck<DbContextHealthCheck<TContext>>("database", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Applies EF Core migrations when the project has them; otherwise creates the schema.
    /// Retries while the database container is still starting.
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(
        this WebApplication app,
        Func<TContext, CancellationToken, Task>? seed = null)
        where TContext : MessagingDbContext
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();

                if (db.Database.GetMigrations().Any())
                {
                    await db.Database.MigrateAsync();
                }
                else
                {
                    await db.Database.EnsureCreatedAsync();
                }

                if (seed is not null)
                {
                    await seed(db, CancellationToken.None);
                }

                logger.LogInformation("Database for {Context} is ready.", typeof(TContext).Name);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Error}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    internal static string BuildConnectionString(IConfiguration configuration, string defaultDatabaseName)
    {
        var explicitConnectionString = configuration.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var section = configuration.GetSection("Database");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = section["Host"] ?? "localhost",
            Port = int.TryParse(section["Port"], out var port) ? port : 5432,
            Database = section["Name"] ?? defaultDatabaseName,
            Username = section["Username"] ?? "postgres",
            Password = section["Password"],
            SslMode = Enum.TryParse<SslMode>(section["SslMode"], ignoreCase: true, out var ssl) ? ssl : SslMode.Prefer,
        };
        return builder.ConnectionString;
    }
}

internal sealed class DbContextHealthCheck<TContext>(TContext db) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to the database.");
}
