# ADR 0002: Cross-Module References via Host Orchestration, Not Direct Module-to-Module Dependencies

## Status

Accepted — 2026-09-17 (Phase 3, Day 33)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

ADR 0001 established one-way dependencies from the host (`FieldOps.Api`) to each module, and left open — deliberately, since no real scenario existed yet — how modules would reference each other once more than one existed.

Day 33 introduces that real scenario: a `FieldOps.Modules.Employees` module, where every `Employee` belongs to exactly one `Organization` (from `FieldOps.Modules.Organizations`). Creating an employee needs to know the referenced organization actually exists.

## Decision

`Employees` does **not** take a project reference to `Organizations`, and never will need one for this kind of relationship. Instead:

1. `Employee.OrganizationId` is a plain `int` — the same relationship a foreign-key column expresses in a database, carrying no knowledge of what it points to.
2. `IEmployeeDirectory.Create(name, organizationId)` does not validate that `organizationId` refers to a real organization — it structurally cannot, since it has no way to ask.
3. **The host validates instead.** `FieldOps.Api`'s `EmployeesController.Create` calls `IOrganizationDirectory.GetById(organizationId)` first; only if that succeeds does it call `IEmployeeDirectory.Create(...)`. The host is the only place in the system with a reference to *both* modules' public interfaces, so it is the natural place for logic that spans them.

## Consequences

* **Positive:** The module dependency graph stays a strict hub-and-spoke shape (host → each module) even as more modules are added — no module ever needs a project reference to another module, so the graph can never become a tangled many-to-many web between modules.
* **Positive:** This mirrors how the eventual Phase 4 split would work anyway — if `Employees` and `Organizations` become separate services, a service can't hold a language-level reference to another service's internal types either; it can only call the other service's public API (or, here, the other module's public interface) and pass plain identifiers. Choosing this pattern now costs nothing extra later.
* **Cost:** Any operation touching more than one module pushes its coordination logic into the host (typically a controller action), rather than into either module. For a single check like this one that's a small, explicit `if`; if cross-module coordination logic grows more complex, it may eventually need its own layer (an "application service" sitting above controllers) rather than living directly in controller actions — not needed yet, revisited if it happens.
* **Not yet solved:** What happens if `Organizations` later needs to *change* in a way that affects existing employees (e.g., an organization is deleted) — nothing today keeps `Employee.OrganizationId` values valid after the fact, since the modules don't know about each other after creation-time validation. This is a referential-integrity problem real databases solve with foreign keys and cascade rules; an in-memory, cross-module equivalent is out of scope until persistence (EF Core) enters the picture.

## Alternatives Considered

* **`Employees` references `Organizations` directly** (a normal project reference, using `IOrganizationDirectory` from inside `Employees`). Rejected — this is the most tempting shortcut, and would still respect ADR 0001's "no reaching into internals" rule, but it starts building a module-to-module dependency graph that has no natural limit: once any module can reference any other module's public interface, nothing stops the graph from becoming as tangled as the single-project alternative ADR 0001 already rejected, just one layer removed. Keeping *all* cross-module coordination in the host (never module-to-module) is a simpler, single rule to hold to.
* **A shared "core" or "kernel" module that both `Organizations` and `Employees` reference**, defining common contracts. Rejected for now — no real shared concept exists yet between these two modules beyond a plain `int` id; introducing a shared project today would be a speculative abstraction with nothing concrete to justify it.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **The application-service layer this ADR anticipated now exists:** cross-module rules moved out of controllers into plain classes with no ASP.NET Core dependency — `EmployeeApplicationService` (Day 34, the exact check this ADR describes) and `WorkOrderAssignmentService` (Day 41), both unit-testable without HTTP. The rule itself is unchanged: coordination lives in the host, never module-to-module.
* **"Not yet solved" remains unsolved, by design:** ADR 0003 confirmed that database-per-module means no cross-module foreign keys, so a dangling `OrganizationId` is still possible in principle. No feature deletes organizations or employees, so the gap has not been exercised; it stays open.
