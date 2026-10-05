# 0005. One database per service on a shared RDS instance

## Context
Services must not share tables, or they become coupled through the schema. Running a separate
RDS instance per service triples cost for a demo workload.

## Decision
Each service owns a separate PostgreSQL **database** (`streamcart_orders`,
`streamcart_inventory`, `streamcart_payments`) on one RDS instance. No service connects to
another's database.

## Consequences
- Logical isolation is enforced; physical isolation can be added later by pointing a service
  at a new instance with no code change.
- The instance is a shared failure and capacity domain. Production would use Multi-AZ and,
  for hot services, dedicated instances or Aurora.
- Schemas are created by EF Core on startup (`EnsureCreated`) until migrations are added; the
  initializer switches to `Migrate()` automatically once a migration exists.
