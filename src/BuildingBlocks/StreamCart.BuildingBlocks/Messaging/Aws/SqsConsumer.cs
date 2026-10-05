using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamCart.BuildingBlocks.Observability;
using StreamCart.BuildingBlocks.Persistence;

namespace StreamCart.BuildingBlocks.Messaging.Aws;

/// <summary>
/// Long-polls this service's SQS queue. Messages are acknowledged (deleted) only after the
/// inbox transaction commits; failures are left on the queue with exponential visibility
/// back-off, and SQS moves them to the dead-letter queue after <c>maxReceiveCount</c>.
/// </summary>
internal sealed class SqsConsumer(
    IAmazonSQS sqs,
    IServiceScopeFactory scopeFactory,
    IDeliveryChaos chaos,
    MessagingStats stats,
    IOptions<MessagingOptions> options,
    ILogger<SqsConsumer> logger) : BackgroundService
{
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundServices || string.IsNullOrWhiteSpace(_options.QueueName))
        {
            logger.LogInformation("SQS consumer disabled: no Messaging:QueueName configured.");
            return;
        }

        var queueUrl = await ResolveQueueUrlAsync(stoppingToken);
        logger.LogInformation("Consuming from {QueueUrl}.", queueUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiveMessageResponse response;
            try
            {
                response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = _options.ReceiveBatchSize,
                    WaitTimeSeconds = _options.ReceiveWaitTimeSeconds,
                    MessageSystemAttributeNames = [ReceiveCountAttribute],
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Receiving from SQS failed; retrying shortly.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            // AWS SDK v4 returns null (not empty) collections when nothing was received.
            foreach (var message in response.Messages ?? [])
            {
                await HandleAsync(queueUrl, message, stoppingToken);
            }
        }
    }

    private async Task HandleAsync(string queueUrl, Message message, CancellationToken ct)
    {
        var envelope = MessageEnvelope.FromJson(message.Body);
        if (envelope is null)
        {
            // Never delete what we cannot read: leave it for the DLQ so a human can inspect it.
            logger.LogError("Unreadable message {SqsMessageId}; leaving it for the dead-letter queue.", message.MessageId);
            return;
        }

        var receiveCount = message.Attributes is not null
            && message.Attributes.TryGetValue(ReceiveCountAttribute, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 1;

        ActivityContext.TryParse(envelope.TraceParent, null, out var parent);
        using var activity = Telemetry.Source.StartActivity($"{envelope.Type} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.message.id", envelope.MessageId.ToString());
        activity?.SetTag("messaging.receive_count", receiveCount);
        activity?.SetTag("streamcart.order_id", envelope.CorrelationId.ToString());

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = envelope.MessageId,
            ["MessageType"] = envelope.Type,
            ["OrderId"] = envelope.CorrelationId,
        });

        try
        {
            await chaos.BeforeProcessingAsync(envelope, ct);

            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IIncomingMessageProcessor>();
            var result = await processor.ProcessAsync(envelope, receiveCount, ct);
            activity?.SetTag("streamcart.processing_result", result.ToString());

            if (chaos.ShouldSkipAcknowledgement(envelope, result))
            {
                // Simulates a crash between commit and ack: SQS will redeliver after the
                // visibility timeout and the inbox must suppress the duplicate.
                stats.AckSkipped();
                logger.LogWarning("Chaos: skipping ack for {Type} {MessageId} to force a redelivery.", envelope.Type, envelope.MessageId);
                await sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, 3, ct);
                return;
            }

            await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stats.HandlerFailed();
            Telemetry.MessagesFailed.Add(1, new KeyValuePair<string, object?>("type", envelope.Type));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Handling {Type} failed on attempt {Attempt}; it will be retried.", envelope.Type, receiveCount);
            await TryBackOffAsync(queueUrl, message.ReceiptHandle, receiveCount, ct);
        }
    }

    private async Task TryBackOffAsync(string queueUrl, string receiptHandle, int receiveCount, CancellationToken ct)
    {
        // 2, 4, 8, 16, 32, 60, 60 ... seconds
        var delay = (int)Math.Min(60, Math.Pow(2, Math.Min(receiveCount, 6)));
        try
        {
            await sqs.ChangeMessageVisibilityAsync(queueUrl, receiptHandle, delay, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not change visibility; default visibility timeout applies.");
        }
    }

    private async Task<string> ResolveQueueUrlAsync(CancellationToken ct)
    {
        var queueName = _options.QueueName!;

        // Never give up (a crashed BackgroundService stops the whole host); back off instead.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await sqs.GetQueueUrlAsync(queueName, ct);
                return response.QueueUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Queue {QueueName} not reachable yet (attempt {Attempt}): {Error}", queueName, attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct);
            }
        }
    }
}
