# ADR 0001: Modular Monolith with One-Way Module Dependencies

## Status

Accepted — 2026-09-17 (Phase 3, Day 32)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

FieldOps's domain spans ten modules (Identity, Organizations, Employees, Customers, Work Orders, Scheduling, Attachments, Notifications, Reporting, Audit Logs). Building all of this inside a single project, with no internal boundaries, would let any part of the codebase reach into any other part's internals — over time this makes it hard to reason about what depends on what, and hard to change one area without breaking another.

A more concrete, project-specific reason: Phase 4 of this roadmap ("Distributed FieldOps") calls for splitting some of these modules into separate services communicating over messaging. If modules are tightly, bidirectionally coupled from the start, that later split becomes far more disruptive. Keeping modules loosely coupled now, even while everything still runs as a single deployable process, keeps that future option realistic without committing to the complexity of real microservices today.

## Decision

FieldOps is structured as a **modular monolith**: one deployable ASP.NET Core Web API host (`FieldOps.Api`), with each business capability implemented as its own class library project (starting today with `FieldOps.Modules.Organizations`).

Two rules govern the boundary:

1. **Dependency direction is one-way.** The host project (`FieldOps.Api`) may reference any module. A module project must never reference the host, and — as more modules are added — must never directly reference another module's internals either. This is enforced today by the simple fact that `FieldOps.Modules.Organizations.csproj` carries zero `<ProjectReference>` entries; if a future module needed to reference `FieldOps.Api`, that would be a build-time signal that something is architecturally wrong.
2. **Each module exposes a narrow public surface; everything else is `internal` — enforced by the compiler, not left as convention.** `IOrganizationDirectory`, the DTOs its methods return (`OrganizationSummary`), and one static `AddOrganizationsModule()` extension method (used to register the module's real implementation with DI) are the *only* `public` types in `FieldOps.Modules.Organizations`. Domain types (`Organization`) and implementation details (`InMemoryOrganizationDirectory`, and later its EF Core replacement) are marked `internal`, making them genuinely inaccessible from `FieldOps.Api` or any other project — not merely undocumented. This was tightened after an initial pass left `Organization`/`InMemoryOrganizationDirectory` `public`; marking them `internal` immediately produced a real `CS0050` (a public interface method can't return an internal type), which is what motivated introducing `OrganizationSummary` as the module's own public shape, distinct from its internal domain entity — the same DTO-boundary discipline StockPilot applied between `IProductStore` and its HTTP-facing `ProductDto`, now applied one layer further out. A second live check (temporarily trying `new InMemoryOrganizationDirectory()` from `FieldOps.Api`) confirmed the compiler genuinely rejects it (`CS0122`).

## Consequences

* **Positive:** Each module's internal structure can change freely (e.g., swapping `InMemoryOrganizationDirectory` for an EF Core-backed implementation) without touching the host or any other module, as long as the public interface's contract holds — the exact same benefit `ISkillCatalog`/`IProductStore` already demonstrated in earlier phases, now applied at a coarser, module-wide grain instead of a single class's grain.
* **Positive:** A module that later needs to become its own service (Phase 4) starts from a position where its external dependencies are already an explicit, narrow interface rather than a tangle of direct references — the extraction is a deployment change, not a full rewrite.
* **Cost:** More projects to manage than a single flat project — ten modules will eventually mean ten class libraries plus the host. This is accepted as the price of the isolation described above, not incurred for its own sake.
* **Not yet decided:** How modules will eventually call *each other* (once more than one exists) — directly through another module's public interface (risking modules depending on modules), through the host orchestrating both, or through an in-process event/message mechanism. This will be revisited once a real cross-module scenario exists (likely once Employees or Work Orders needs to reference an Organization), rather than designed speculatively today.

## Alternatives Considered

* **Single flat project for all of FieldOps.** Rejected — no enforced boundary at all; every module would be able to reach into every other module's internals from day one, working directly against Phase 4's stated later goal.
* **Full microservices from the start.** Rejected — Phase 3 (Weeks 7-12) is explicitly the modular-monolith phase; splitting into real services with independent deployment and network communication is Phase 4's stated scope (Weeks 13+), not this phase's. Introducing that complexity now would be building ahead of the roadmap's own sequencing.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **"Not yet decided" is now decided:** ADR 0002 (Day 33) settled how modules reference each other — the host orchestrates, modules never reference modules.
* **Five modules, not ten:** Organizations, Employees, Customers, Work Orders and Audit Logs were built. Notifications became a separate service instead of a module (ADR 0005); Identity is handled in the host (ADR 0010); the remaining modules were never needed by the project's scope. The "ten class libraries" cost did not materialize.
* **The in-memory implementations are gone:** every module's `InMemory*Directory` was replaced by an EF Core implementation (Day 48 onward), exactly the swap this ADR's first consequence predicted — the public interfaces stayed, their callers did not change at the time.
* **The core decision still holds:** the module projects still reference no other module and not the host, and their entities and `DbContext`s are still `internal`.
