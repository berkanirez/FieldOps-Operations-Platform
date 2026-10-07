# ADR 0004: `WorkOrderCompletedEvent` Is (For Now) a Domain Event, Not an Integration Event

## Status

Accepted — 2026-09-27 (Phase 4, Day 70)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

Days 66-69 built a real, working RabbitMQ producer/consumer pipeline: `WorkOrdersController.Complete` publishes `WorkOrderCompletedEvent` to a named `fanout` exchange, and two independent consumers (`WorkOrderCompletedEventConsumer`, `WorkOrderCompletedAuditConsumer`) each receive their own copy of it. Throughout, this has simply been called "an event" or "a domain event," without ever distinguishing that term from a related but meaningfully different one: an **integration event**.

The distinction matters specifically because of what Phase 4 does next. Week 15's roadmap topic is extracting real, independently-deployed services out of FieldOps (a notification service, a reporting service). The moment either of `WorkOrderCompletedEvent`'s current consumers moves into its own separately-deployed process, the question "can we still freely change this event's shape" gets a very different answer than it has today — and that answer needs to be decided deliberately, not discovered by accident when a deployed consumer breaks.

**Domain event:** a record of something that has already happened, published and consumed entirely within one bounded context (here: the single `FieldOps.Api` process/deployment). Every publisher and every consumer is compiled, deployed, and versioned together. Nothing outside that boundary knows or cares that the event exists.

**Integration event:** the same kind of "something happened" record, but crossing a real deployment boundary — published by one independently-deployed service, consumed by another. It is, in effect, a **contract**: a promise that the shape (and meaning) of the message will not change out from under whoever is consuming it, because that consumer's own release cycle, ownership, and codebase are no longer under the publisher's control.

## Decision

`WorkOrderCompletedEvent`, as it exists today (Day 66-69), **is a domain event, not an integration event** — even though it is physically carried over RabbitMQ, the same transport a real integration event would use. What makes it a domain event is not the transport; it is that `RabbitMqEventPublisher`, `WorkOrderCompletedEventConsumer`, and `WorkOrderCompletedAuditConsumer` all live inside the exact same `FieldOps.Api` project, built and deployed as one unit. Nothing external to this single deployable currently reads this event.

Concretely, this means: today, `WorkOrderCompletedEvent`'s shape (its five fields) can be freely changed — a field renamed, added, or removed — in the same commit that updates every consumer, with no coordination, no versioning scheme, and no backward-compatibility concern. This is not a gap; it is the correct, honest state for an event with no consumer outside its own deployment.

This event will need to be **re-classified as an integration event** — with the consequences below actually implemented, not just acknowledged — at the exact moment either of its current consumers (or a new one) is extracted into a genuinely separate, independently-deployed service (Week 15's actual work). That moment is not today.

## Consequences

* **Positive (today):** No versioning ceremony, no shared "contracts" package, no schema registry, no backward-compatibility discipline is needed yet — all of that would be pure ceremony against a problem that doesn't exist yet, the same "no premature complexity without a real problem" reasoning ADR 0003 already applied to Redis and database-per-module.
* **Positive (today):** Because everything is one deployment, a mistake in the event's shape is caught immediately by `dotnet build` (every consumer recompiles against the same type) — there is no way for a publisher and a consumer to silently drift out of sync the way there would be across a real service boundary.
* **Cost (deferred, not solved):** When Week 15 extracts a real service, this ADR's classification flips, and real work follows: the event's schema will need to be documented as a stable contract (likely versioned, e.g. `WorkOrderCompletedEventV1`), changes will need backward-compatible tolerance (new consumers must not break on an old shape, and vice versa during a rollout), and the event may need to move to a shared location both the publisher's and the new service's codebases can reference without one depending on the other's internals — none of which exists today, and none of which should be built now.
* **No code changed today.** This ADR is a classification and a documented trigger condition ("re-classify when a real service extraction happens"), not an implementation.

## Alternatives Considered

* **Treat `WorkOrderCompletedEvent` as an integration event already, and add versioning/contract discipline now, ahead of Week 15.** Rejected — there is no real external consumer yet to protect against; adding this now would be exactly the premature complexity this workspace has consistently avoided (ADR 0003's Redis/database-per-module reasoning, and Day 63's `IAiProvider` abstraction built only once a real need existed). It would also risk guessing wrong about what the real contract needs to look like before a real second deployment exists to inform that design.
* **Don't classify at all — just call everything "an event" and decide later, case by case.** Rejected — without a named, agreed distinction, the moment a service actually gets extracted in Week 15, there would be no existing checklist or trigger prompting the "this now needs versioning" conversation; it would be easy to extract the service and simply forget that the event's contract status changed underneath it.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **The trigger fired on Day 76:** `FieldOps.NotificationService` was extracted into its own deployable, so `WorkOrderCompletedEvent` became an **integration event**, as this ADR predicted. The in-process audit consumer still exists, but the event's shape is now a contract.
* **How the contract is held today:** not by a shared `Contracts` project. The notification service keeps its own copy of the record (`src/FieldOps.NotificationService/WorkOrderCompletedEvent.cs`), with a comment explaining why; both sides agree on the same JSON shape. Nothing enforces that agreement — `dotnet build` can no longer catch a drift, which is exactly the cost this ADR described.
* **Still not built:** schema versioning (`...V1`) and tolerance rules for rolling deployments. The event's shape has not changed since extraction, so no real versioning problem has occurred yet. The safe rule while both copies exist: add fields only; never rename or remove one while the other side may still be deployed.
