using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;

namespace StreamCart.BuildingBlocks.Messaging.Aws;

/// <summary>
/// Publishes envelopes to a single SNS topic. The <c>type</c> message attribute drives the
/// per-queue subscription filter policies, so each service only receives what it handles.
/// </summary>
internal sealed class SnsMessageTransport(
    IAmazonSimpleNotificationService sns,
    IOptions<MessagingOptions> options) : IMessageTransport
{
    public async Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var topicArn = options.Value.TopicArn
            ?? throw new InvalidOperationException("Messaging:TopicArn is not configured.");

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = envelope.ToJson(),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["type"] = new() { DataType = "String", StringValue = envelope.Type },
                ["source"] = new() { DataType = "String", StringValue = envelope.Source },
            },
        };

        await sns.PublishAsync(request, cancellationToken);
    }
}
