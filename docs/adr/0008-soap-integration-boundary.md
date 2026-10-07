# ADR 0008: SOAP — What It Is, and Where FieldOps Would Actually Use It

## Status

Accepted — 2026-09-29 (Phase 4, Day 82)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

Every external interaction FieldOps has built so far — REST endpoints (Phase 2 onward), RabbitMQ messaging (Week 13-15), Elasticsearch (Week 16) — has been JSON-based and built by us, or built recently by someone else following modern conventions. Week 16's roadmap names a genuinely different kind of interaction: SOAP, a protocol that predates REST's dominance and is still the *only* interface many older, real-world enterprise systems expose — accounting/ERP systems, some banking and payment systems, and a number of government systems. FieldOps has no existing SOAP interaction, but the realistic scenario this ADR is written against is a real one: once a completed work order needs to be billed, FieldOps would need to hand that fact to whatever accounting/ERP system a customer's organization already runs — and if that system is an older one, SOAP may be the only door in.

This ADR does not build that integration (that's Day 83's "SOAP client integration"); it states what SOAP actually is, why it still matters, and where FieldOps's boundary with it would sit, before any code is written — the same "explain the rule before the implementation" precedent ADR 0001/0002 established.

**SOAP (Simple Object Access Protocol):** an XML-based protocol for exchanging structured messages, almost always over HTTP. Unlike REST, which treats an interaction as an operation on a *resource* (`GET /workorders/12`, `POST /workorders`), SOAP treats it as a *remote procedure call* — "invoke this specific operation, with these specific parameters" (`SubmitInvoice(workOrderId, amount, customerId)`), always through the same HTTP verb (almost always `POST`), with the actual operation name and parameters carried entirely inside the XML body, not the URL.

**The core building blocks:**
* **WSDL (Web Services Description Language)** — an XML document a SOAP service publishes describing every operation it offers, each operation's expected input/output shapes, and the types involved. It is SOAP's equivalent of an OpenAPI/Swagger document, except WSDL predates OpenAPI by over a decade, is XML rather than JSON, and is far stricter and more verbose — most SOAP client tooling (including .NET's own) works by reading a WSDL and generating strongly-typed client code from it automatically, rather than a developer writing HTTP calls by hand.
* **SOAP Envelope** — every SOAP message (request or response) is wrapped in a fixed XML structure: an outer `<Envelope>`, an optional `<Header>` (metadata — authentication tokens, routing information), and a `<Body>` (the actual operation call or its result).
* **SOAP Fault** — SOAP's equivalent of REST's HTTP status codes. A failed operation doesn't necessarily return a `4xx`/`5xx` HTTP status (the HTTP layer often still reports `200 OK`, since HTTP is only the transport) — instead, the response body itself is a `<Fault>` element inside the envelope, carrying its own fault code and message. This is a genuinely different error-handling shape than every ASP.NET Core controller in this codebase has used so far (`BadRequest`, `StatusCode(403, ...)`, `ProblemDetails`), and it's a real trap for someone assuming "check the HTTP status code" is always enough.

## Decision

FieldOps will treat any future SOAP integration (e.g. submitting billing data to a customer's accounting/ERP system) as a **boundary to be isolated, not a shape to let leak into the rest of the codebase**. Concretely:

1. A SOAP client (whether hand-written or generated from a WSDL via tooling) will live behind its own interface — the same seam shape already used repeatedly in this codebase (`INotificationSender` Day 51, `IAiProvider` Day 63, `IWorkOrderSearchIndex` Day 79) — so that `WorkOrdersController` and every other caller depends only on a plain, FieldOps-shaped interface, never on generated SOAP client types, XML serialization attributes, or `SoapFault` exception types directly.
2. This isolation is specifically what Week 16's next roadmap topic, **anti-corruption layer**, means in practice: a translation layer that converts between FieldOps's own domain model (its own shapes, its own vocabulary) and whatever awkward, legacy shape the external SOAP system demands — so that a change in the external system's WSDL, or a future replacement of that system entirely, never forces a change anywhere outside that one translation layer.
3. SOAP Faults will be caught and translated into FieldOps's own error conventions (the same `BadRequest`/`ProblemDetails` shapes already used everywhere else) at that same boundary — no controller or service outside the anti-corruption layer should ever need to know what a `SoapFault` looks like.

None of this is implemented today. This ADR exists so that Day 83's actual client code is built *against* an already-decided boundary, rather than discovering where that boundary should be while writing the integration — exactly ADR 0005's precedent for the notification-service extraction.

## Consequences

* **Positive:** Day 83's SOAP client integration has a target to build toward: one interface, one implementation behind it, no SOAP-specific type ever surfacing outside that implementation.
* **Positive:** This ADR gives "anti-corruption layer" — an abstract-sounding term on the roadmap — a concrete, motivated reason to exist in this codebase specifically, rather than being introduced as a pattern for its own sake (the same discipline this workspace has applied to every prior pattern, from the Outbox pattern to `IWorkOrderSearchIndex`).
* **Cost (deferred, not solved today):** No real WSDL, no real external SOAP service, and no actual .NET SOAP tooling (`dotnet-svcutil`, `System.ServiceModel`) has been touched yet — all genuinely Day 83's work, once a concrete external service (real or a realistic stand-in) is chosen to integrate against.
* **No code changed today.** This ADR is a conceptual foundation and a stated boundary, not an implementation.

## Alternatives Considered

* **Wait until Day 83 to think about the boundary at all, and let the generated SOAP client types be used directly wherever billing logic needs them.** Rejected — this is exactly the "distributed monolith" mistake ADR 0005 already rejected for service extraction, just at the type-boundary level instead of the deployment level: a future change to the external accounting system's WSDL (or replacing it entirely) would then ripple into `WorkOrdersController` and anywhere else that touched the generated types directly.
* **Treat SOAP as effectively obsolete and skip this topic, since FieldOps has no real SOAP need today.** Rejected — the roadmap places this here specifically because real, working .NET backend roles routinely still encounter legacy SOAP systems (accounting, banking, government, and many enterprise B2B integrations), and being unable to reason about WSDL, envelopes, and SOAP Faults is a genuine, non-hypothetical gap for a junior/junior+ .NET developer to have.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **Built as decided on Day 83:** `IBillingAmountSpeller` with `DataAccessBillingAmountSpeller` behind it, a generated `System.ServiceModel` client against a public SOAP service, and SOAP faults translated at the boundary. ADR 0009 names it the codebase's clearest anti-corruption layer.
* **Package versions pinned (Day 123):** the `System.ServiceModel.*` packages had a floating `4.10.*` range; they are now pinned to `4.10.3` (SECURITY_REVIEW F11).
