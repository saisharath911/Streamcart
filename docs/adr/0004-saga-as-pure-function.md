# 0004. Saga logic as a pure function

## Context
Saga bugs hide in rare interleavings: a reply arriving after a timeout, a duplicate event
after cancellation, a release overtaking a reserve. Testing those through queues and
databases is slow and flaky.

## Decision
Model the saga as `OrderSaga.Decide(OrderSnapshot, trigger) -> SagaDecision`: no I/O, no clock,
no randomness. The decision contains the next status, the messages to send and a
human-readable narrative. Handlers are thin adapters that load, decide, persist and enqueue.

## Consequences
- Every transition, including out-of-order and duplicate messages, has a unit test that runs
  in milliseconds (`OrderSagaTests`).
- The narrative stored on the order timeline makes production behaviour explainable to
  non-engineers (it is what the dashboard shows).
- A framework such as MassTransit's state machines was not used, so the mechanics stay
  visible; a team could adopt one later without changing the contracts.
