namespace StreamCart.BuildingBlocks.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>ARN of the SNS topic every service publishes to.</summary>
    public string? TopicArn { get; set; }

    /// <summary>Name of this service's SQS queue. Leave empty to disable consuming.</summary>
    public string? QueueName { get; set; }

    /// <summary>Custom endpoint, e.g. http://localhost:4566 for LocalStack. Null in AWS.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>Set to false in tests to run without background publishing/consuming.</summary>
    public bool EnableBackgroundServices { get; set; } = true;

    public int OutboxBatchSize { get; set; } = 50;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>After this many failed publish attempts an outbox row is parked for inspection.</summary>
    public int OutboxMaxAttempts { get; set; } = 10;

    public int ReceiveBatchSize { get; set; } = 10;

    public int ReceiveWaitTimeSeconds { get; set; } = 20;
}
