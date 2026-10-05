# 0001. Orchestrated saga instead of distributed transactions or choreography

## Context
Placing an order changes data owned by three services. Two-phase commit across services is
not available with SQS and PostgreSQL in separate services, couples availability (one slow
participant blocks all), and holds locks across the network.

Choreography (each service reacts to the previous service's event) works for two steps but
spreads the workflow across code bases: nobody owns "what happens when payment fails", and
adding a step means changing several services.

## Decision
Use an **orchestrated saga** owned by the Orders service. Orders sends commands
(`ReserveInventory`, `ProcessPayment`) and reacts to replies. Every forward step has a
compensating action (`ReleaseInventory`, `RefundPayment`).

## Consequences
- The whole workflow, including failure paths, is readable in one file (`OrderSaga.cs`).
- The system is eventually consistent: an order is visible as `AwaitingPayment` before it is
  confirmed. The UI shows this explicitly instead of hiding it.
- Orders is a logical coordinator, not a bottleneck: it holds no locks and keeps state in its
  own database.
- Every step needs a timeout, or a lost reply leaves an order stuck (handled by
  `SagaTimeoutWatcher`).
