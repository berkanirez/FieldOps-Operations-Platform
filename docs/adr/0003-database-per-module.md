# ADR 0003: Each Module Owns Its Own Database

## Status

Accepted — 2026-09-21 (Phase 3, Day 48)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

FieldOps has been entirely in-memory since Day 32 — no module has ever persisted data. This became a real problem when Week 10's roadmap topic (Redis, cache-aside) was about to be introduced: caching only solves a real problem when it protects something genuinely slower than memory (a database, a network call). Without a real backing store, "cache-aside" would have been a mechanical exercise with nothing real to justify it — the same "no premature complexity without a real problem" concern this workspace has applied consistently since Phase 1.

Separately, `docs/REQUIREMENTS_MATRIX.md` already listed EF Core evidence as expected across "Phase 1–4," while `docs/ROADMAP.md`'s week-by-week breakdown of Phase 3 never actually scheduled adding it to FieldOps — a real inconsistency between the two planning documents, caught by Berkan's own question rather than by re-reading the docs closely enough beforehand.

Given persistence is being added now, a new question arises that didn't exist for StockPilot (a single, non-modular API): should FieldOps's modules share one database, or does each module get its own?

## Decision

**Each module owns its own database**, not just its own `DbContext` class. `FieldOps.Modules.Organizations` today uses `FieldOpsOrganizations`; each subsequent module (`Employees`, `WorkOrders`, `Customers`) will get its own separately-named database when its turn comes, all on the same local SQL Server instance for now (`localhost\SQLEXPRESS`) purely as a deployment convenience, not an architectural coupling.

Concretely:

1. `OrganizationsDbContext` is `internal` to `FieldOps.Modules.Organizations` — the host never references it, never configures it directly, and never runs a query against it itself.
2. `OrganizationsModule.AddOrganizationsModule(IServiceCollection, string connectionString)` is the only thing the host calls — it hands over a connection string (read from its own `appsettings.json`, under a module-specific key, `FieldOpsOrganizationsDb`) and never sees `OrganizationsDbContext` or `EfOrganizationDirectory` by name.
3. `IOrganizationDirectory` (the module's public contract) doesn't change at all — `OrganizationsController` and every other caller keep working unmodified, exactly as ADR 0001 intended when it made the concrete implementation swappable.

## Consequences

* **Positive:** No module's database schema can ever be reached by another module's queries, even by accident — there is no shared connection, no shared schema, nothing to accidentally `JOIN` across. This is the strongest possible enforcement of ADR 0001/0002's "modules don't know about each other" rule, extended from code into data.
* **Positive:** This is the closest a modular monolith can get to Phase 4's eventual reality without actually being distributed yet — when (if) `Organizations` is later extracted into its own service, its database doesn't need to move or be split from anyone else's; it already stands alone.
* **Cost:** No real foreign keys, and no real transactions, can ever span two modules' data — e.g., nothing enforces that `Employee.OrganizationId` refers to a row that still exists in `Organizations`' own database, even now that real persistence exists. This is the exact same gap ADR 0002 already flagged and deferred "until persistence enters the picture" — persistence has now arrived, and the gap is confirmed to remain, by design, not by oversight. A cross-module data-consistency problem (e.g., an organization deleted while employees still reference it) would need to be solved the way real distributed systems solve it — eventual consistency, outbox/inbox patterns (Phase 4's actual topic) — not with a database-level constraint.
* **Cost:** More moving parts today — one connection string, one `DbContext`, one migration history per module, instead of one of each for the whole app. Accepted as the honest price of the isolation guarantee above.
* **Test infrastructure:** `FieldOps.Api.Tests` needed a Testcontainers-managed, disposable SQL Server (`FieldOpsApiFactory`, mirroring StockPilot's Day 28 `StockPilotApiFactory`) — the moment any module gets a real database, integration tests that exercise it can no longer be "pure in-memory fakes with no external dependency," and GitHub Actions' CI workflow comment describing them that way (accurate since Day 34) went stale the same day this ADR was accepted.

## Alternatives Considered

* **One shared `FieldOpsDb` database, with each module's tables inside it** (e.g., `Organizations.Organizations`, `Employees.Employees` as separate schemas in one physical database). Rejected — while this would still keep each module's own `DbContext` scoped to only its own tables in code, the physical database itself would be one shared, breakable boundary: nothing at the infrastructure level would stop a future developer (or a migration mistake) from writing a cross-schema `JOIN`, the exact coupling ADR 0002 already ruled out at the code level. Database-per-module makes that mistake structurally impossible, not just discouraged by convention.
* **Keep everything in-memory a while longer, defer Redis until a "real" persistence phase.** Rejected — Redis (Week 10) and later Week 11's health checks/observability topics only make sense against something that can genuinely be slow, fail, or need monitoring; deferring persistence further would have pushed that same unresolved tension into every subsequent week instead of resolving it now.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **All five modules now have their own database** (`FieldOpsOrganizations`, `FieldOpsEmployees`, `FieldOpsCustomers`, `FieldOpsWorkOrders`, `FieldOpsAuditLogs`), and the extracted notification service has a sixth (`FieldOpsNotifications`, ADR 0005).
* **More hosts than SQL Express:** the same databases also run on a containerized SQL Server (Docker Compose, Day 59) and ran on Azure SQL (Days 108–110). The decision did not change; only the server did.
* **Transient-fault retries (Day 111):** every module's `UseSqlServer` now enables `EnableRetryOnFailure`; explicit transactions run inside `CreateExecutionStrategy().ExecuteAsync`. The notification service's own `DbContext` does not have it yet (see ADR 0011).
* **The cross-module consistency gap** is still the accepted cost; the Outbox/Inbox patterns (Days 71, 73) were used for messaging, not to repair cross-module references.
