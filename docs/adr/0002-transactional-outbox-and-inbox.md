# 0002. Transactional outbox and inbox for reliable messaging

## Context
"Save to the database, then publish to SNS" fails in two ways: the process can crash between
the two steps (the event is lost), or publishing can succeed and the database commit fail
(a phantom event). SQS standard queues deliver at least once, so consumers also see duplicates.

## Decision
- **Outbox:** messages are written to an `outbox_messages` table in the same transaction as the
  state change. A background publisher relays committed rows to SNS and marks them published.
  Rows are claimed with `FOR UPDATE SKIP LOCKED` so several replicas can publish in parallel.
- **Inbox:** each consumer records `(message_id, consumer)` in `inbox_processed_messages` in the
  same transaction as its handler's changes. A repeat delivery finds the row and is skipped.
  The composite primary key also catches two concurrent deliveries of the same message.

## Consequences
- Delivery is at least once end to end; processing is effectively once.
- Publishing adds up to one poll interval (250 ms) of latency. Acceptable for this domain;
  PostgreSQL `LISTEN/NOTIFY` could remove it if needed.
- Outbox and inbox tables grow; a production deployment needs a retention job
  (for example, delete published rows older than 7 days).
- The Chaos Lab's "duplicate storm" scenario demonstrates the inbox working live.
