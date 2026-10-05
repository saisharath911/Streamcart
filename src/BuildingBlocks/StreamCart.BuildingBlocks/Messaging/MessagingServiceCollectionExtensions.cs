using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StreamCart.BuildingBlocks.Messaging.Aws;
using StreamCart.BuildingBlocks.Observability;
using StreamCart.BuildingBlocks.Persistence;

namespace StreamCart.BuildingBlocks.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Wires SNS publishing, SQS consumption, the transactional outbox and inbox for a service.
    /// </summary>
    public static IServiceCollection AddStreamCartMessaging<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
        where TContext : MessagingDbContext
    {
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new ServiceIdentity(serviceName));
        services.AddSingleton<MessagingStats>();
        services.TryAddSingleton<IDeliveryChaos, NoDeliveryChaos>();

        services.TryAddSingleton<IAmazonSimpleNotificationService>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            var config = new AmazonSimpleNotificationServiceConfig();
            ApplyEndpoint(config, o);
            return new AmazonSimpleNotificationServiceClient(config);
        });

        services.TryAddSingleton<IAmazonSQS>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            var config = new AmazonSQSConfig();
            ApplyEndpoint(config, o);
            return new AmazonSQSClient(config);
        });

        services.TryAddSingleton<IMessageTransport, SnsMessageTransport>();
        services.AddScoped<IOutbox, EfOutbox<TContext>>();
        services.AddScoped<IIncomingMessageProcessor, IncomingMessageProcessor<TContext>>();

        // Both check MessagingOptions when they start and exit quietly if disabled.
        services.AddHostedService<OutboxPublisher<TContext>>();
        services.AddHostedService<SqsConsumer>();

        return services;
    }

    /// <summary>Registers a handler for the message type, keyed by its wire name.</summary>
    public static IServiceCollection AddMessageHandler<TMessage, THandler>(this IServiceCollection services)
        where TMessage : IIntegrationMessage
        where THandler : MessageHandler<TMessage>
    {
        services.AddKeyedScoped<IMessageHandler, THandler>(MessageTypeRegistry.NameOf<TMessage>());
        return services;
    }

    private static void ApplyEndpoint(Amazon.Runtime.ClientConfig config, MessagingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            // LocalStack / custom endpoint.
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
    }
}
