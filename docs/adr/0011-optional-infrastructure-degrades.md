# ADR 0011: SQL Server Is Essential; Redis, RabbitMQ and Elasticsearch Are Optional and Must Degrade, Not Fail

## Status

Accepted — 2026-10-06 (Phase 6, Day 126). **Recorded retrospectively:** the behaviour was built piece by piece between Day 51 and Day 116, each time in response to a real failure; this ADR states the rule those fixes add up to, and the places where the code does not yet follow it.

## Context

FieldOps depends on four pieces of infrastructure. Each was added for a different reason, and each has failed at least once during this project:

* **Redis** (Day 48) holds a cache-aside status report and idempotency keys. A Redis outage first produced 500s on the report (fixed Day 108), then 11% errors under load because `RedisTimeoutException` does not derive from `RedisException` (fixed Day 112), and then — the worst case — requests hanging for more than 11 minutes in StackExchange.Redis's command backlog, found with `dotnet-stack` while investigating a test suite that had slowed from 8.5 to 37 minutes (fixed Day 116 with `BacklogPolicy.FailFast`).
* **RabbitMQ** (Day 66) carries `WorkOrderCompletedEvent`. Publishing directly from the request lost events when the broker was down, which led to the transactional outbox (Day 71) and retry with backoff (Day 72).
* **Elasticsearch** (Day 79) holds a searchable copy of work orders. An unreachable cluster made calls wait far too long and, because its client reports failures through `IsValidResponse` instead of throwing, could have looked like "zero matches" (Days 80, 87).
* **SQL Server** holds every module's data. Transient faults on Azure SQL failed requests that a retry would have saved (Day 111).

Without a stated rule, every new dependency gets the same question answered again, usually after an outage. The question is: which dependencies may take the API down with them, and which must not?

## Decision

1. **SQL Server is essential.** It is the source of truth for every module. When it is unavailable, requests that need it fail with an error response — there is nothing correct to degrade to. Transient faults are retried (`EnableRetryOnFailure` in every module, explicit transactions inside `CreateExecutionStrategy().ExecuteAsync`), and readiness reports the instance as not ready.
2. **Everything else is optional.** A Redis, RabbitMQ or Elasticsearch outage must not stop the API from serving the requests that do not need that dependency, and must not make any request hang.
3. **Each optional dependency has a stated degraded mode:**
   * **Redis — serve without the cache.** The report is computed from SQL Server and the result returned; cache reads, writes and invalidation failures are caught (`RedisException or RedisTimeoutException`) and logged. `AbortOnConnectFail = false` lets the app start without Redis; `BacklogPolicy.FailFast` makes commands fail immediately while disconnected instead of queueing. The cache warmer skips its work while `IsConnected` is false.
   * **Redis — idempotency fails open.** If the idempotency store is unreachable, the request is processed without the duplicate check, rather than rejected. This is a deliberate trade-off: during a Redis outage a retried create can produce a duplicate work order. Rejecting every create while Redis is down was judged worse for a field-service tool than a rare duplicate an Admin can see and remove.
   * **RabbitMQ — deliver later.** The event is written to the outbox in the same transaction as the state change, so the request succeeds without the broker. The background publisher retries each unpublished message on every tick until it succeeds; consumers reconnect with exponential backoff. Notifications are delayed, never lost.
   * **Elasticsearch — search is unavailable, everything else works.** Indexing goes through the same outbox, so the index catches up after an outage (and can be rebuilt from SQL Server, Day 81). Calls are wrapped in a Polly pipeline: a 2-second timeout inside a circuit breaker, so an outage costs at most a short wait and then fails immediately while the circuit is open. A failed search is an error, never an empty result — "no matches" would be a wrong answer, not a missing one.
4. **Catch the exact failures, not everything.** Degradation is implemented with filtered catches of the dependency's own exception types where practical, so that bugs in our own code still surface instead of being silently "degraded".

## Consequences

* **Positive:** Verified live, not only in tests. On Day 88 RabbitMQ and Elasticsearch were stopped together and a work order was completed: `200 OK`, the API stayed up, and within about ten seconds of restarting them every outbox row was published and the search document was up to date. On Day 123 the report endpoint was load-tested with Redis stopped: 413 req/s at p95 39 ms (426 req/s, p95 40 ms with Redis) — still serving, at the price of 12,483 SQL queries instead of 18.
* **Positive:** A new dependency now has a checklist: is it essential or optional; if optional, what is its degraded mode, what is its exact failure exception, and can it ever block?
* **Cost:** Degraded modes have their own costs: a missing cache means more SQL load exactly when something is already wrong; fail-open idempotency can produce duplicates; delayed events mean late notifications. Each is accepted above, explicitly.
* **Cost:** Every degraded path needs its own test, because it only runs during an outage. The Redis paths have automated tests (cache resilience, cache warmer, report service with a failing Redis proxy); the outbox retry and the Elasticsearch circuit breaker were verified live (Days 71–72, 87–88) but have no automated test.

**Where the code does not yet follow this ADR (open):**

* **Readiness still includes Redis.** `/health/ready` checks Redis as well as SQL Server (Day 56, before Redis was optional). In Kubernetes a Redis outage would therefore take every API Pod out of traffic — the opposite of rule 2. The Redis check should report `Degraded` rather than `Unhealthy`, or leave readiness. Not changed today: it is a behaviour change and deserves its own test.
* **The notification service has no SQL Server retry.** Its `NotificationServiceDbContext` is registered without `EnableRetryOnFailure`, unlike the five modules.
* **A failed search returns 500.** The exception reaches the global exception handler and becomes a 500 ProblemDetails; `503 Service Unavailable` would tell the client the truth — "temporarily unavailable, try again" — as the AI summary endpoint already does.
* **Only one SQL Server database is in readiness.** The check covers `FieldOpsWorkOrders` only, a deliberate narrowing because all databases share one server in every environment used so far.

## Alternatives Considered

* **Treat every dependency as essential (fail if any is down).** Rejected — the cache, the message broker and the search index exist to make the system faster or more decoupled; letting any of them take the whole API down would make the system less available than it was without them.
* **Catch all exceptions around every dependency call.** Rejected as the default — it would hide our own bugs behind "degraded" behaviour. Broad catches remain only at background-worker loop boundaries, where one bad tick must not stop the host.
* **Fail closed for idempotency.** Considered and rejected for now (see rule 3); it would be the right choice where a duplicate is costly, such as payments.
