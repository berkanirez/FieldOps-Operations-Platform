# ADR 0009: Naming the Pattern — Anti-Corruption Layer

## Status

Accepted — 2026-09-29 (Phase 4, Day 84)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

Week 16's last roadmap topic is "anti-corruption layer." By the time this day started, FieldOps had already built the thing itself, more than once, without ever calling it that: `INotificationSender` (Day 51), `IAiProvider` (Day 63), `IWorkOrderSearchIndex` (Day 79), and most recently `IBillingAmountSpeller` (Day 83) all share one shape — a FieldOps-owned interface standing between the rest of the codebase and one specific external dependency's own types and failure modes.

This ADR does not introduce new code. It exists to answer a real question this repeats-itself-four-times pattern raises: is "anti-corruption layer" just another name for the same seam used everywhere, or does it mean something more specific? Answering that honestly matters more than checking a roadmap box — using a precise term loosely is exactly the kind of thing a junior/junior+ interview or code review would catch.

**Anti-corruption layer (ACL):** a term from Eric Evans's *Domain-Driven Design*. It names a translation layer placed at the boundary between two systems whose own internal *models* — their vocabulary, their shapes, their failure modes — are meaningfully different, specifically to stop the foreign model's shape from leaking into (and gradually deforming, "corrupting") the local one.

## Decision

All four interfaces above are the same underlying idea — dependency inversion at an external boundary — but they sit at different points on one spectrum, and only some of them are genuinely worth calling an anti-corruption layer:

* **`INotificationSender` / `IAiProvider`:** the external dependency's own "model" is trivial or entirely hidden behind its own SDK's idiomatic .NET shape. There is barely a foreign model to protect against — these are ordinary **seams** (the same Dependency Inversion idea every one of these interfaces uses), not anti-corruption layers in the fuller DDD sense.
* **`IWorkOrderSearchIndex`:** Elasticsearch's model (documents, mappings, a Query DSL, non-throwing failed responses) is genuinely different from FieldOps's own relational, exception-based world — closer to a real ACL, though the translation is fairly thin (a small denormalized document, a few query-building calls).
* **`IBillingAmountSpeller`:** the clearest case. SOAP's model — XML envelopes, WSDL-generated proxy types with awkward wrapper shapes (`NumberToDollarsRequestBody`, `NumberToDollarsResponse.Body.NumberToDollarsResult`), and `FaultException` as its native error channel — is about as structurally distant from FieldOps's own `decimal`/`string`/`InvalidOperationException` world as any dependency this codebase has touched. `DataAccessBillingAmountSpeller` is the genuine, textbook anti-corruption layer: it exists specifically because letting SOAP's own shape reach even one line outside that class would mean FieldOps's domain code now has to speak two models instead of one.

**The distinguishing question going forward:** not "is there an interface here" (that's true of all four), but "would using the dependency's own native types/exceptions directly, anywhere outside this one class, force the rest of the codebase to understand a genuinely foreign model?" When the answer is yes — as it clearly is for SOAP, and less clearly but still meaningfully for Elasticsearch — the interface is doing anti-corruption-layer work, not just ordinary dependency inversion.

## Consequences

* **Positive:** The term now has a precise, defensible meaning in this codebase, tied to concrete examples on both ends of the spectrum, rather than being applied loosely to "any interface over an external thing."
* **Positive:** Week 16 — Elasticsearch, search indexing, index synchronization, rebuild strategy, SOAP concepts, SOAP client integration, anti-corruption layer — is now fully complete, with every topic backed by real, live-verified code or a stated architectural decision.
* **No code changed today**, beyond a single clarifying comment added to `DataAccessBillingAmountSpeller.cs` naming it explicitly as this codebase's clearest anti-corruption layer example.

## Alternatives Considered

* **Treat every seam interface in this codebase as "an anti-corruction layer" for roadmap purposes, without distinguishing degrees.** Rejected — this would make the term meaningless (a vocabulary word applied to everything is not adding any real distinction), and would misrepresent, in an interview or review setting, what `INotificationSender` actually is.
* **Build a brand-new anti-corruction-layer example from scratch today, purely to have a dedicated one.** Rejected — `IBillingAmountSpeller` already is one, built yesterday for a real (if learning-focused) reason; writing a second, purpose-built example only to check a box would be exactly the kind of premature, unmotivated code this workspace has consistently avoided.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **No change:** all four interfaces and their implementations still exist as described; no new external dependency has been added since, so the spectrum this ADR defines has not needed a new entry.
