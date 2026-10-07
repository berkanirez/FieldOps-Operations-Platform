# ADR 0007: What "Independently Deployable" Actually Requires

## Status

Accepted — 2026-09-28 (Phase 4, Day 78)

Reviewed against the code on 2026-10-06 (Phase 6, Day 126) — see **Later Developments**.

## Context

Day 76 extracted `FieldOps.NotificationService` as a genuinely separate process with its own database, but every verification since then (including Day 76's own live demo) ran both services by hand, in two separate terminals. Day 78's task was to bring both up with a single `docker compose up --build`, using the `fieldops-notification-service` entry `docker-compose.yml` already gained on Day 76. That worked — but succeeding at it exposed a real question Week 15's roadmap names directly: does "both services start with one command" mean the same thing as "these are independently deployable"? It does not, and this ADR states why, before the difference gets papered over by the fact that the demo now looks smoother.

## Decision

A single `docker-compose.yml` that defines both `fieldops-api` and `fieldops-notification-service` is a **local development convenience**, not evidence of independent deployability. The two remain genuinely independent in the ways ADR 0005 already established — separate processes, separate databases, no shared connection string, no `ProjectReference`, communicating only through RabbitMQ — but this one file re-couples them at exactly the layer ADR 0005 didn't touch: **how they get built, versioned, and released.**

Concretely, three things would need to be true before this repo's setup counts as real independent deployment, none of which are true today:

1. **Separate build/release pipelines.** Today, `docker compose up --build` rebuilds both images from the same invocation, at the same time, from the same commit of the same repository. A real independent release would let `FieldOps.NotificationService` ship a new version on Tuesday while `FieldOps.Api` stays on last week's build — nothing in today's setup prevents that in principle (their Dockerfiles are already separate, per Day 76), but nothing in today's setup does it either.
2. **Independent versioning.** Neither service has a version number at all today. Two services that are "independently deployable" need to be able to answer "which version of you is running right now" separately from each other; today the honest answer for both is "whatever the last `docker compose up --build` built."
3. **No single command that redeploys both.** `docker compose up` is, by construction, one command that can restart or rebuild either or both services together. A genuinely independent setup would make it structurally awkward (not just against convention) to redeploy `FieldOps.NotificationService` by touching anything that also affects `fieldops-api` — e.g. two separate deployment targets (two separate Kubernetes Deployments, two separate cloud App Services), not two services entries in one compose file.

None of this is a defect in today's work — `docker-compose.yml` is doing exactly the job Day 78 needed from it: **prove both services can be built and run from clean, from a single set of instructions, without hand-editing anything.** That is a real, necessary step (and was missing before today — Day 76's own demo had to be run by hand). It's simply not the same claim as "these are independently deployable in production," and this repo should not accidentally start treating it as such.

## Consequences

* **Positive:** The demo from Day 76 (create/assign/start/complete a work order, notification and audit landing in their own separate logs and databases) was successfully re-run through `docker compose up --build` alone — a real, structural improvement over needing two people manually running `dotnet run` in two terminals, and now the natural way for anyone (including Berkan, later, without this session's memory of Day 76's manual steps) to bring the whole stack up.
* **Positive:** Live-caught, unplanned confirmation of Day 72's retry/backoff logic: `FieldOps.NotificationService`'s container started before RabbitMQ had finished accepting connections, logged a connection failure, and reconnected on its own moments later — the exact behavior Day 72 built and demonstrated in a different environment, now proven to also hold inside this compose-orchestrated one.
* **Cost (deferred, not solved today):** A real CI/CD pipeline (per-service builds, per-service versioning, per-service deploy triggers) does not exist for either service. Phase 5/6 of this roadmap (Weeks 19-20: Kubernetes, GitHub Actions delivery pipeline) is where that work actually belongs — building it now, for a project that still runs on one developer's machine, would be exactly the premature complexity this workspace has consistently avoided.
* **No code changed today.** `docker-compose.yml` and both Dockerfiles were exercised, not modified; migrations were applied by hand against the containerized SQL Server (still not automated — a separately-noted, deliberate gap, consistent with every prior day's manual-migration precedent).

## Alternatives Considered

* **Treat today's successful `docker compose up --build` as "independent deployment done."** Rejected — this is the exact conflation this ADR exists to prevent. Orchestration (one command starts many things) and independent deployability (each thing can change on its own schedule, released and versioned on its own) are different properties; a system can have all of the first and none of the second.
* **Build a real CI/CD pipeline today to close the gap properly.** Rejected for now — that is genuinely Week 19-20's material (GitHub Actions, container registries, Kubernetes), and building it in isolation today, without the Kubernetes/registry concepts those weeks introduce, would mean redoing it later anyway once the right tools are actually understood.

## Later Developments (reviewed 2026-10-06, Day 126)

The decision above is kept as written; these notes record what happened afterwards.

* **Partly closed in Phase 5:** GitHub Actions now builds and publishes container images to GitHub Container Registry on every push to `master` (Day 104), each tagged with the commit SHA — an exact, never-reused version a running container can be traced back to (point 2). The API and the frontend have separate Kubernetes Deployments (Days 99–103), which can be rolled out independently (point 3).
* **Still not true for the notification service:** the publish job builds only the API and frontend images, both from the same commit and the same pipeline run, and the notification service has no Kubernetes manifest. Points 1 and 3 remain open for it; `docker-compose.yml` is still the only way it is deployed.
* **Migrations are still applied by hand** (README, CI and Azure alike); no migration job exists.
