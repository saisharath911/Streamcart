# Architecture Decision Records

Short records of decisions that shaped StreamCart, why they were made, and what they cost.
Format: context, decision, consequences. A decision is changed by adding a new ADR that supersedes it.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-orchestrated-saga.md) | Orchestrated saga instead of distributed transactions or choreography | Accepted |
| [0002](0002-transactional-outbox-and-inbox.md) | Transactional outbox and inbox for reliable messaging | Accepted |
| [0003](0003-sns-sqs-fan-out.md) | SNS fan-out to per-service SQS queues with filter policies | Accepted |
| [0004](0004-saga-as-pure-function.md) | Saga logic as a pure function | Accepted |
| [0005](0005-database-per-service.md) | One database per service on a shared RDS instance | Accepted |
| [0006](0006-realtime-updates-with-signalr.md) | Real-time dashboard updates with SignalR | Accepted |
| [0007](0007-ecs-fargate-compute.md) | ECS Fargate for compute | Accepted |
