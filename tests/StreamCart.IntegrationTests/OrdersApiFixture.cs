using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StreamCart.BuildingBlocks.Messaging;
using Testcontainers.PostgreSql;

namespace StreamCart.IntegrationTests;

/// <summary>
/// Boots the real Orders API against a throw-away PostgreSQL container. The SNS transport is
/// swapped for an in-memory capture so tests can assert on what the outbox actually published,
/// and the SQS consumer is disabled so tests drive incoming messages explicitly.
/// </summary>
public sealed class OrdersApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public CapturingTransport Transport { get; } = new();

    public Task InitializeAsync() => _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Messaging:QueueName", string.Empty);
        builder.UseSetting("Messaging:TopicArn", "arn:aws:sns:us-east-1:000000000000:test");
        builder.UseSetting("Messaging:OutboxPollInterval", "00:00:00.050");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageTransport>();
            services.AddSingleton<IMessageTransport>(Transport);
        });
    }
}

public sealed class CapturingTransport : IMessageTransport
{
    private readonly ConcurrentQueue<MessageEnvelope> _published = new();

    public IReadOnlyCollection<MessageEnvelope> Published => _published;

    public Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        _published.Enqueue(envelope);
        return Task.CompletedTask;
    }

    public async Task<MessageEnvelope> WaitForAsync(Guid orderId, string type, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var match = _published.FirstOrDefault(e => e.CorrelationId == orderId && e.Type == type);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No {type} was published for order {orderId}.");
    }
}
