using Amazon.CDK;
using StreamCart.Infra;

var app = new App();

_ = new StreamCartStack(app, "StreamCart", new StackProps
{
    Description = "StreamCart: event-driven order platform (ECS Fargate, SNS/SQS, RDS PostgreSQL, CloudFront)",
    Env = new Amazon.CDK.Environment
    {
        Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
        Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION") ?? "us-east-1",
    },
});

Tags.Of(app).Add("project", "streamcart");
Tags.Of(app).Add("managed-by", "cdk");

app.Synth();
