# 0006. Real-time dashboard updates with SignalR

## Context
The dashboard should show saga progress as it happens, without polling every order.

## Decision
The Orders service hosts a SignalR hub and pushes `orderUpdated` after each saga transition
commits (post-commit hook, so the UI never shows a state that rolled back). The client uses
WebSockets only with `skipNegotiation`, so no sticky sessions are needed behind the ALB.

## Consequences
- Updates arrive within milliseconds of the transition.
- Broadcasts reach only clients connected to the same instance. Orders runs as one task; to
  scale it out, add a Redis backplane (ElastiCache) or move to API Gateway WebSockets.
- Clients that reconnect re-fetch the order list to catch up on missed pushes.
