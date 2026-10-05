# 0007. ECS Fargate for compute

## Context
The services are long-running: they host HTTP APIs, a SignalR hub, an SQS long-poll loop and
background publishers. Options: Lambda, ECS Fargate, EKS.

## Decision
Run each service as an ECS Fargate service behind one ALB with path-based routing, defined in
AWS CDK (C#).

## Consequences
- Background loops and WebSockets work naturally; Lambda would need the outbox publisher and
  consumers split into separate functions and API Gateway WebSockets for the hub.
- No cluster to operate, unlike EKS.
- The inventory service scales on SQS backlog (messages visible), which tracks customer-facing
  delay better than CPU.
- Deployment uses the ECS circuit breaker with automatic rollback.
- Cost: roughly $3-4 per day (about $100/month) for the whole stack, mostly NAT gateway, ALB, RDS and three small tasks.
  The deploy workflow is manual and the README documents `cdk destroy`.
