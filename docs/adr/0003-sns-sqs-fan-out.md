# 0003. SNS fan-out to per-service SQS queues with filter policies

## Context
Services need durable, independently consumable message streams with retries and
dead-lettering. Options considered: SQS only (point to point), SNS + SQS, EventBridge,
and Amazon MSK (Kafka).

## Decision
Publish every message to one SNS topic with a `type` message attribute. Each service has its
own SQS queue (plus a DLQ) subscribed with a **filter policy** listing the types it handles,
and **raw message delivery** so the queue body is the envelope itself.

## Consequences
- Publishers do not know who consumes a message; adding a consumer is a new subscription.
- Each service scales, retries and dead-letters independently.
- Standard queues do not guarantee order. The saga tolerates reordering explicitly (late and
  duplicate messages are tested). FIFO topics/queues would add ordering at a throughput cost.
- EventBridge would add schema registry and archive/replay but higher per-event latency and
  cost; Kafka would add replay and ordering per partition but significant operational weight.
  Both remain options behind the `IMessageTransport` abstraction.
- LocalStack provides the same topology locally (`deploy/localstack/init-aws.sh`).
