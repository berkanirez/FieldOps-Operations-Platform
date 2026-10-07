# ADR 0005: The Notification Service's Boundary and Data Ownership

## Status

Accepted — 2026-09-28 (Phase 4, Day 75)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

Days 66-74 built a complete, working RabbitMQ pipeline for `WorkOrderCompletedEvent` — publishing (`RabbitMqEventPublisher`, via the Outbox pattern), and two consumers (`WorkOrderCompletedEventConsumer` for notifications, `WorkOrderCompletedAuditConsumer` for audit logging), both idempotent (Inbox pattern) and both resilient to permanent failures (dead-letter queue). All of this, however, still lives inside the single `FieldOps.Api` deployment — both consumers reach their Inbox/dead-letter tracking through `IWorkOrderDirectory`, a direct C# interface call into the `WorkOrders` module's own database, something only possible because everything is compiled and deployed together.

Week 15's roadmap topic is extracting a genuinely separate notification service. Before writing any extraction code, this ADR — following ADR 0001/0002's precedent of stating the rule before the implementation — defines exactly what that service will and will not own, so the extraction has a written target to build toward rather than being decided ad hoc mid-refactor.

## Decision

**The notification service, once extracted, owns:**
* Its own notification-sending logic and history (today: `LoggingNotificationSender`, tomorrow: a real provider).
* Its own Inbox tracking (which messages it has already processed) and its own dead-letter records for messages it could not process.
* Its own database, entirely separate from any of FieldOps.Api's five module databases (ADR 0003's database-per-module principle, extended one level further: database-per-*service*, not just per-module).

**The notification service never owns, and never directly accesses:**
* `WorkOrder`, `Employee`, `Organization`, or `Customer` data, or any of their databases. It has no connection string to any of them and no reference to `IWorkOrderDirectory` or any other module interface — that dependency is exactly what today's `WorkOrderInboxStore` has, and exactly what must be removed the moment this service is actually extracted, since a cross-deployment, cross-network C# interface reference is not a real option.
* Any knowledge of *why* a work order was completed, who assigned it, or its lifecycle history — it only ever knows what `WorkOrderCompletedEvent` tells it.

**The only channel between `FieldOps.Api` and the notification service is `WorkOrderCompletedEvent`, published over RabbitMQ.** No shared database, no direct HTTP call between them for this flow, no back-channel. This is deliberately the same "modules don't know about each other, only the host orchestrates" shape ADR 0001/0002 already established for the modular monolith — here, RabbitMQ plays the role the host used to play, and the two sides don't even share a host anymore.

**Consequence for ADR 0004:** the moment this extraction actually happens (Day 76+, not today), `WorkOrderCompletedEvent` is re-classified from a domain event to an integration event, exactly as ADR 0004 anticipated. Concretely, this means: its shape becomes a contract (existing fields cannot be silently renamed or removed once both sides are deployed independently); it needs a stable, agreed-upon home both codebases can reference without one depending on the other's internals (a shared `FieldOps.Contracts` project is the most likely shape, to be decided when extraction actually begins); and a schema change on one side can no longer be verified by `dotnet build` alone, the way it can today while everything compiles together.

## Consequences

* **Positive:** The extraction itself (Day 76+) becomes a matter of *moving* code to satisfy a boundary that's already been decided, not *discovering* the boundary while moving code — the riskier order, since architectural mistakes made under refactoring pressure are exactly how boundaries end up leaky.
* **Positive:** Today's `IInboxStore` abstraction (Day 73) already anticipated this — `EventConsumerBase<TEvent>` never depended on `IWorkOrderDirectory` directly, only on `IInboxStore`. The extraction's actual work is replacing `WorkOrderInboxStore` (backed by `IWorkOrderDirectory`) with a new implementation backed by the notification service's own, new database — `EventConsumerBase` itself needs no change at all.
* **Cost:** The notification service will need its own EF Core setup, its own migrations, its own connection string, its own small database — genuinely more infrastructure than today's single shared `WorkOrders` database, accepted as the honest price of real independent deployability (the same cost ADR 0003 already accepted for database-per-module, now paid again one level up).
* **Cost (deferred, not solved today):** where the shared `WorkOrderCompletedEvent` type will actually live once two separate codebases need it, and how its version will be communicated, is explicitly not decided by this ADR — that decision is deferred to the day extraction actually begins, when the real constraints (deployment tooling, whether the notification service is even in the same solution or a separate repository) are known.

## Alternatives Considered

* **Let the notification service keep reading `IWorkOrderDirectory`/the `WorkOrders` database directly, just running in a separate process.** Rejected outright — this is not a real extraction, it is a distributed monolith: two processes sharing one database is one of the most common ways teams end up with "microservices" that must be deployed together anyway, defeating the entire point of Week 15's topic.
* **Give the notification service read access to a copy/replica of `WorkOrder` data so it can look up richer context than the event alone provides.** Rejected for today's scope — `WorkOrderCompletedEvent`'s existing fields (`WorkOrderId`, `Title`, `CustomerId`, `CompletedAtUtc`) are already sufficient for both current consumers; introducing a data-replication concern (keeping a copy in sync) would be solving a problem that doesn't exist yet, the same "no premature complexity" reasoning behind every prior ADR in this repo.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **Implemented on Day 76** exactly as decided: a separate worker project with no `ProjectReference` to `FieldOps.Api`, its own `NotificationServiceDbContext` (inbox and failed-attempt tables only), its own database and migrations, and RabbitMQ as the only channel. The consumer logic came across unchanged in shape: the service has its own copy of `EventConsumerBase` (no shared reference, same reason as the event record), and only the `IInboxStore` implementation is genuinely new, backed by the service's own database, as predicted.
* **The deferred "where does the event type live" question:** answered for now with a deliberate duplicate record in each codebase (see the ADR 0004 note), not a shared project. A shared contracts package becomes worth its cost when there is a second consumer service or a separate repository.
* **The cost of its own database was real and caught late:** on Day 125 a clean-room run of the README showed that the quickstart never created `FieldOpsNotifications`; the API worked, and only the notification service's logs showed the failure. The README now applies all six migration targets.
