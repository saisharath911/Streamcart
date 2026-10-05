using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.SQS;
using Constructs;
using ElbHealthCheck = Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using InstanceType = Amazon.CDK.AWS.EC2.InstanceType;

namespace StreamCart.Infra;

/// <summary>
/// Everything StreamCart needs in one stack:
///   CloudFront ─┬─ S3 (dashboard)
///               └─ ALB ── ECS Fargate: orders | inventory | payments ── RDS PostgreSQL
///   SNS topic "streamcart-events" ──(filter policy per service)──> SQS queue + DLQ per service
/// </summary>
public sealed class StreamCartStack : Stack
{
    private sealed record ServiceSpec(
        string Name,
        string Folder,
        int Priority,
        string[] Paths,
        string[] Handles,
        bool AutoScaleOnBacklog);

    // Each service subscribes only to the message types it handles.
    private static readonly ServiceSpec[] Services =
    [
        new("orders", "Orders", 10,
            ["/api/orders", "/api/orders/*", "/hubs/*"],
            ["InventoryReserved", "InventoryRejected", "InventoryReleased", "PaymentSucceeded", "PaymentFailed", "PaymentRefunded"],
            AutoScaleOnBacklog: false), // SignalR broadcast is per instance until a Redis backplane is added (ADR-0006).
        new("inventory", "Inventory", 20,
            ["/api/products", "/api/products/*", "/api/reservations", "/api/inventory/*"],
            ["ReserveInventory", "ReleaseInventory"],
            AutoScaleOnBacklog: true),
        new("payments", "Payments", 30,
            ["/api/payments", "/api/payments/*", "/api/chaos", "/api/chaos/*"],
            ["ProcessPayment", "RefundPayment"],
            AutoScaleOnBacklog: false), // Chaos settings live in memory; keep one instance for a predictable demo.
    ];

    public StreamCartStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."));

        // ---------------- Network ----------------
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 2,
            NatGateways = 1, // one NAT keeps the demo affordable; use one per AZ in production
        });

        // ---------------- Messaging ----------------
        var alarms = new Topic(this, "Alarms", new TopicProps { DisplayName = "StreamCart alarms" });
        var events = new Topic(this, "Events", new TopicProps { TopicName = "streamcart-events" });

        // ---------------- Database ----------------
        var database = new DatabaseInstance(this, "Postgres", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps { Version = PostgresEngineVersion.VER_17 }),
            InstanceType = InstanceType.Of(InstanceClass.T4G, InstanceSize.MICRO),
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_WITH_EGRESS },
            Credentials = Credentials.FromGeneratedSecret("streamcart"),
            AllocatedStorage = 20,
            StorageEncrypted = true,
            MultiAz = false,
            BackupRetention = Duration.Days(1),
            DeletionProtection = false,
            RemovalPolicy = RemovalPolicy.DESTROY, // demo stack: `cdk destroy` removes everything
        });

        // ---------------- Compute ----------------
        var cluster = new Cluster(this, "Cluster", new ClusterProps
        {
            Vpc = vpc,
            ContainerInsightsV2 = ContainerInsights.ENABLED,
        });

        var alb = new ApplicationLoadBalancer(this, "Alb", new ApplicationLoadBalancerProps
        {
            Vpc = vpc,
            InternetFacing = true,
            IdleTimeout = Duration.Seconds(300), // keeps SignalR WebSockets open
        });

        var listener = alb.AddListener("Http", new BaseApplicationListenerProps
        {
            Port = 80,
            Open = true,
            DefaultAction = ListenerAction.FixedResponse(404, new FixedResponseOptions
            {
                ContentType = "application/json",
                MessageBody = "{\"error\":\"No route\"}",
            }),
        });

        foreach (var spec in Services)
        {
            AddService(spec, repoRoot, cluster, listener, database, events, alarms);
        }

        // ---------------- Dashboard (S3 + CloudFront) ----------------
        var siteBucket = new Bucket(this, "Site", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        // One origin for UI and API: no CORS, and WebSockets pass through CloudFront.
        var apiBehavior = new BehaviorOptions
        {
            Origin = new LoadBalancerV2Origin(alb, new LoadBalancerV2OriginProps
            {
                ProtocolPolicy = OriginProtocolPolicy.HTTP_ONLY,
                ReadTimeout = Duration.Seconds(60),
            }),
            AllowedMethods = AllowedMethods.ALLOW_ALL,
            CachePolicy = CachePolicy.CACHING_DISABLED,
            OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER,
            ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        };

        var distribution = new Distribution(this, "Cdn", new DistributionProps
        {
            DefaultBehavior = new BehaviorOptions
            {
                Origin = S3BucketOrigin.WithOriginAccessControl(siteBucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            },
            AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
            {
                ["/api/*"] = apiBehavior,
                ["/hubs/*"] = apiBehavior,
            },
            DefaultRootObject = "index.html",
        });

        _ = new BucketDeployment(this, "DeployDashboard", new BucketDeploymentProps
        {
            Sources = [Source.Asset(Path.Combine(repoRoot, "frontend", "dist"))],
            DestinationBucket = siteBucket,
            Distribution = distribution,
            DistributionPaths = ["/*"],
        });

        _ = new CfnOutput(this, "DashboardUrl", new CfnOutputProps
        {
            Value = $"https://{distribution.DistributionDomainName}",
            Description = "Open this to use the Saga Lab.",
        });
        _ = new CfnOutput(this, "AlarmTopicArn", new CfnOutputProps
        {
            Value = alarms.TopicArn,
            Description = "Subscribe your email to receive dead-letter queue alarms.",
        });
    }

    private void AddService(
        ServiceSpec spec,
        string repoRoot,
        Cluster cluster,
        ApplicationListener listener,
        DatabaseInstance database,
        Topic events,
        Topic alarms)
    {
        var id = spec.Folder;

        // Queue + dead-letter queue, subscribed to the topic with a filter policy.
        var dlq = new Queue(this, $"{id}Dlq", new QueueProps
        {
            QueueName = $"streamcart-{spec.Name}-dlq",
            RetentionPeriod = Duration.Days(14),
            Encryption = QueueEncryption.SQS_MANAGED,
        });

        var queue = new Queue(this, $"{id}Queue", new QueueProps
        {
            QueueName = $"streamcart-{spec.Name}",
            VisibilityTimeout = Duration.Seconds(60),
            Encryption = QueueEncryption.SQS_MANAGED,
            DeadLetterQueue = new DeadLetterQueue { Queue = dlq, MaxReceiveCount = 5 },
        });

        events.AddSubscription(new SqsSubscription(queue, new SqsSubscriptionProps
        {
            RawMessageDelivery = true,
            FilterPolicy = new Dictionary<string, SubscriptionFilter>
            {
                ["type"] = SubscriptionFilter.StringFilter(new StringConditions { Allowlist = spec.Handles }),
            },
        }));

        // Any message in a DLQ means something needs a human: page on the first one.
        new Alarm(this, $"{id}DlqAlarm", new AlarmProps
        {
            AlarmDescription = $"Messages are landing in the {spec.Name} dead-letter queue.",
            Metric = dlq.MetricApproximateNumberOfMessagesVisible(new MetricOptions { Period = Duration.Minutes(1) }),
            Threshold = 1,
            EvaluationPeriods = 1,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
            TreatMissingData = TreatMissingData.NOT_BREACHING,
        }).AddAlarmAction(new SnsAction(alarms));

        // Task definition: one container built from the shared Dockerfile.
        var task = new FargateTaskDefinition(this, $"{id}Task", new FargateTaskDefinitionProps
        {
            Cpu = 256,
            MemoryLimitMiB = 512,
        });

        task.AddContainer("app", new ContainerDefinitionOptions
        {
            Image = ContainerImage.FromAsset(repoRoot, new AssetImageProps
            {
                File = "src/Services/Dockerfile",
                BuildArgs = new Dictionary<string, string> { ["SERVICE"] = spec.Folder },
                Exclude = ["frontend", "infra", "docs", "load", "tests", ".git", "**/bin", "**/obj", "**/node_modules", "**/cdk.out"],
            }),
            Logging = LogDriver.AwsLogs(new AwsLogDriverProps
            {
                StreamPrefix = spec.Name,
                LogRetention = RetentionDays.ONE_MONTH,
            }),
            PortMappings = [new PortMapping { ContainerPort = 8080 }],
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                // Empty values override the local defaults in appsettings.json.
                ["ConnectionStrings__Default"] = string.Empty,
                ["Messaging__ServiceUrl"] = string.Empty,
                ["Database__Host"] = database.DbInstanceEndpointAddress,
                ["Database__Port"] = database.DbInstanceEndpointPort,
                ["Database__Name"] = $"streamcart_{spec.Name}",
                ["Database__SslMode"] = "Require",
                ["Messaging__Region"] = Region,
                ["Messaging__TopicArn"] = events.TopicArn,
                ["Messaging__QueueName"] = queue.QueueName,
            },
            // Credentials never appear in plain text: ECS injects them from Secrets Manager.
            Secrets = new Dictionary<string, EcsSecret>
            {
                ["Database__Username"] = EcsSecret.FromSecretsManager(database.Secret!, "username"),
                ["Database__Password"] = EcsSecret.FromSecretsManager(database.Secret!, "password"),
            },
        });

        var service = new FargateService(this, $"{id}Service", new FargateServiceProps
        {
            Cluster = cluster,
            TaskDefinition = task,
            DesiredCount = 1,
            MinHealthyPercent = 100,
            CircuitBreaker = new DeploymentCircuitBreaker { Rollback = true },
            EnableExecuteCommand = true,
        });

        // Least privilege: publish to the one topic, consume only its own queue.
        events.GrantPublish(task.TaskRole);
        queue.GrantConsumeMessages(task.TaskRole);
        database.Connections.AllowDefaultPortFrom(service, $"{spec.Name} to PostgreSQL");

        listener.AddTargets($"{id}Targets", new AddApplicationTargetsProps
        {
            Port = 8080,
            Protocol = ApplicationProtocol.HTTP,
            Targets = [service],
            Priority = spec.Priority,
            Conditions = [ListenerCondition.PathPatterns(spec.Paths)],
            DeregistrationDelay = Duration.Seconds(10),
            HealthCheck = new ElbHealthCheck
            {
                Path = "/health/ready",
                HealthyHttpCodes = "200",
                Interval = Duration.Seconds(15),
            },
        });

        if (spec.AutoScaleOnBacklog)
        {
            // Scale on queue backlog rather than CPU: the backlog is what customers feel.
            var scaling = service.AutoScaleTaskCount(new EnableScalingProps { MinCapacity = 1, MaxCapacity = 4 });
            scaling.ScaleOnMetric($"{id}BacklogScaling", new BasicStepScalingProps
            {
                Metric = queue.MetricApproximateNumberOfMessagesVisible(new MetricOptions { Period = Duration.Minutes(1) }),
                AdjustmentType = AdjustmentType.CHANGE_IN_CAPACITY,
                ScalingSteps =
                [
                    new ScalingInterval { Upper = 10, Change = -1 },
                    new ScalingInterval { Lower = 100, Change = 1 },
                    new ScalingInterval { Lower = 500, Change = 2 },
                ],
            });
        }
    }
}
