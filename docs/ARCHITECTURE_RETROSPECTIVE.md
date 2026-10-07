# FieldOps — Architecture Retrospective

Written at the end of the project (Phase 6, Day 129). For every boundary and abstraction in FieldOps: **what** it is, **why** it exists (the concrete problem it solved, and when), **the rule** it enforces, and **its industry name**, so it can be recognized and discussed outside this codebase. The ADRs hold the full reasoning for the big decisions; this document is the map.

The guiding rule throughout: no abstraction before a real problem needed it. Most entries below were introduced on the day a concrete problem appeared, and the "Not done" section matters as much as the rest.

---

## 1. Structural boundaries

| What | Why (problem, when) | Rule it enforces | Industry name |
|---|---|---|---|
| **Module projects** (`FieldOps.Modules.Organizations`, `Employees`, `Customers`, `WorkOrders`, `AuditLogs`), each a class library | One project would let any code reach any other code's internals; Phase 4 planned to split pieces out (Day 32) | A module never references the host or another module; entities and `DbContext` are `internal`, enforced by the compiler | **Modular monolith**; module = **bounded context** (lightweight, DDD) — [ADR 0001](adr/0001-modular-monolith-one-way-dependencies.md) |
| **Public module interface + DTOs** (`IWorkOrderDirectory` returning `WorkOrderSummary`) | Making entities `internal` produced CS0050: a public method can't return an internal type (Day 32) | The host sees contracts, never entities; the implementation can be swapped (in-memory → EF Core, Day 48) without touching callers | **Facade** over the module; **published interface**; DTO boundary |
| **`Add<Module>Module(connectionString)`** registration methods | The host must wire each module without knowing its internal types | Only the module knows its implementation and `DbContext` | **Composition root** pattern; module registration via DI extensions |
| **Host orchestration** (controllers, then application services, coordinate modules) | Employees needed to know an organization exists (Day 33) without referencing the Organizations module | Cross-module rules live only in the host; dependency graph stays hub-and-spoke | **Orchestration** (as opposed to choreography); [ADR 0002](adr/0002-cross-module-references-via-host-orchestration.md) |
| **Application services** (`EmployeeApplicationService`, `WorkOrderAssignmentService`) | Cross-module checks inside controllers mixed HTTP and business rules and needed HTTP to test (Days 34, 41) | Controllers translate HTTP; services hold rules and have no ASP.NET Core dependency, so they are unit-testable | **Application service layer** (DDD / layered architecture); **thin controllers** |
| **Result objects** (`EmployeeCreationResult`, `WorkOrderAssignmentResult`) | Services needed to report expected failures (not found, wrong tenant) without exceptions or HTTP types | Expected outcomes are values; the controller maps them to status codes | **Result pattern** |
| **Database per module** | Persistence arrived (Day 48); one shared database would let any module join any table | No module can query another module's data, even by accident; no cross-module foreign keys | **Database per service** (applied to modules) — [ADR 0003](adr/0003-database-per-module.md) |
| **Notification service** (`FieldOps.NotificationService`), separate process and database | Week 15 extraction; the only part that just reacts to events | No compile-time reference to the API; RabbitMQ is the only channel | **Microservice extraction**, strangler-style; **service boundary / data ownership** — [ADR 0005](adr/0005-notification-service-boundary.md) |

## 2. Seams around external dependencies

| What | Why | Rule | Industry name |
|---|---|---|---|
| **`IEventPublisher`** → `RabbitMqEventPublisher` | Publishing should not tie business code to RabbitMQ's client (Day 67) | Callers publish an event; how it travels is hidden | **Port and adapter** (hexagonal); **dependency inversion** |
| **`INotificationSender`** → `LoggingNotificationSender` | No real email/SMS provider yet (Day 51) | One registration changes when a real provider arrives | **Seam**; **strategy** by DI; port/adapter |
| **`IAiProvider`** → `FakeAiProvider` | Summarizing evidence notes needed an AI call without committing to a vendor (Day 63) | Services depend on a capability, not a vendor SDK | **Port/adapter**; **test double** as the default implementation |
| **`IWorkOrderSearchIndex`** → `ElasticsearchWorkOrderSearchIndex` | Elasticsearch's model (documents, Query DSL, non-throwing failures) differs from FieldOps's (Day 79) | Search results come back as FieldOps types; failures become exceptions | **Repository-like gateway**; partial **anti-corruption layer** — [ADR 0009](adr/0009-anti-corruption-layer.md) |
| **`IBillingAmountSpeller`** → `DataAccessBillingAmountSpeller` | SOAP's generated types and `FaultException` must not leak (Day 83) | SOAP stays inside one class; faults are translated | **Anti-corruption layer** (DDD), textbook case — [ADR 0008](adr/0008-soap-integration-boundary.md) |
| **`EmployeePasswordHasher`** wrapping `PasswordHasher<object>` | Password hashing needed one place, plus a dummy hash for timing equalization (Day 122) | Controllers never touch the hashing API directly | **Adapter / wrapper** |
| **`IAuditLogWriter`** (AuditLogs module) | Audit entries are written by several flows | Same public-interface rule as every module | Module **published interface** |

## 3. Messaging and reliability

| What | Why | Rule | Industry name |
|---|---|---|---|
| **Outbox table + `OutboxPublisher`** | Publishing directly lost events when RabbitMQ was down (Day 71) | The event is written in the same transaction as the state change; a background worker publishes until it succeeds | **Transactional outbox** (solves **dual write**) |
| **`IInboxStore` + `ProcessedMessages`** | RabbitMQ delivers at least once; a redelivered message would notify twice (Day 73) | Each consumer records processed message ids and skips repeats | **Inbox pattern**; **idempotent consumer** |
| **`EventConsumerBase<TEvent>`** | Every consumer needed the same connect/retry/inbox/dead-letter/tracing logic (Days 68–74) | Subclasses implement only `HandleAsync` | **Template method** pattern |
| **Dead-letter queue** after three attempts | A message that always fails would retry forever (Day 74) | Poison messages are parked and logged | **Dead-letter queue / poison message handling** |
| **Exponential backoff reconnect** (capped at 30 s) | The broker starting after the consumer crashed it (Day 72) | Retry with growing delay, never a tight loop | **Retry with exponential backoff** |
| **Idempotency keys** on create (`IdempotencyService`) | A client retrying after a timeout would create a duplicate (Day 54) | Same key, same response | **Idempotency key** (as in payment APIs) |

## 4. Cross-cutting concerns

| What | Why | Rule | Industry name |
|---|---|---|---|
| **`WorkOrderReportService` with Redis** + invalidation + `WorkOrderReportCacheWarmer` | Reports were recomputed on every request (Days 48–50) | Read cache, fall back to SQL, delete on write | **Cache-aside**; **cache warming** |
| **Fail-fast Redis + filtered catches** | Redis outages caused 500s, missed timeouts, then 11-minute hangs (Days 108, 112, 116) | An optional dependency must fail quickly and be caught by exact type | **Graceful degradation**; **fail fast** — [ADR 0011](adr/0011-optional-infrastructure-degrades.md) |
| **Polly pipeline** (circuit breaker around a 2 s timeout) for Elasticsearch | An unreachable cluster made calls hang (Day 87) | Bound every call; stop calling a failing dependency for a while | **Circuit breaker**, **timeout** (resilience patterns) |
| **`CorrelationIdMiddleware`** | Logs for one request couldn't be found together (Day 55) | Every log line in a request carries its correlation ID | **Correlation ID**; middleware / **chain of responsibility** |
| **`FieldOpsTracing`, `FieldOpsMetrics`** | Following a request through RabbitMQ; measuring outbox lag (Days 85–86) | One named `ActivitySource` and `Meter` per process; trace context in message headers | **Distributed tracing** (OpenTelemetry), **context propagation**, custom metrics |
| **`SqlServerHealthCheck`, `RedisHealthCheck`**, live vs ready | Orchestrators need to know whether to restart or just stop routing (Day 56) | Liveness checks nothing external; readiness checks dependencies | **Health check** pattern; liveness/readiness probes |
| **Fallback authorization policy + `ClaimsPrincipalExtensions`** | Identity came from forgeable headers (Day 121) | Every endpoint requires a token; identity only from validated claims | **Secure by default**; **claims-based identity** — [ADR 0010](adr/0010-identity-and-tenant-from-token-claims.md) |
| **Rate limiter policies** (per tenant claim, per login IP) | Burst protection, then the header-keyed bypass (Days 53, 122) | Limits run after authentication, keyed on trusted identity | **Rate limiting** (fixed window), **partitioned limiter** |
| **`SecurityEvents`** log category | Failed logins and rejections were invisible (Day 123) | Security events go to one category with structured fields | **Security event logging / audit trail** |
| **ProblemDetails + global exception handler** | Unhandled errors returned bare 500s (Day 123) | One error shape, no internals | **RFC 9457 Problem Details** |

## 5. Deliberately not done (and why)

| Not done | Why not |
|---|---|
| **Generic repository over EF Core** (`IRepository<T>`) | `DbContext` already is a unit of work and `DbSet` a repository. A generic layer would hide `IQueryable` (and with it Day 113's lesson about where filtering runs) and add nothing. Each module has a purpose-built interface instead. |
| **AutoMapper** | Manual mapping (`ToSummary`, `ToDto`) keeps data flow visible and refactor-safe; mapping code is small. |
| **MediatR / CQRS** | No problem in the project needed a mediator or separate read/write models; controllers calling services is direct and debuggable. The read side does have its own query shapes (paged list, status counts), which is the useful part of CQRS without the machinery. |
| **Clean Architecture template** (Domain/Application/Infrastructure projects per feature) | Boundaries were introduced only when a problem required them; the module split already gives separation where it matters. |
| **Splitting more microservices** | Only the notification service had a reason to deploy independently. Splitting the rest would add network failures and eventual consistency for no gain ([ADR 0007](adr/0007-independent-deployment.md)). |
| **Shared contracts package** | One consumer service; two documented copies are cheaper than a versioned package for now ([ADR 0004](adr/0004-domain-events-vs-integration-events.md)). |
| **Mocking EF Core** | Integration tests against real SQL Server (Testcontainers) instead; in-memory providers behave differently from SQL. |
| **Minimal APIs** | Controllers were a deliberate choice for the learning phases (workspace rule); the boundaries above would be the same with minimal APIs. |

## 6. If I started again

* **Identity from the first secured endpoint.** Header-based identity (Phase 3) was a learning shortcut that every role and ownership check was built on; fixing it in Week 21 touched every controller and client. I'd issue and require tokens as soon as roles appeared.
* **Async from the start.** Synchronous data access worked in development and starved the thread pool under load (Day 118). Async end to end costs almost nothing when written first.
* **A health-check rule per dependency, decided once.** Redis was added to readiness before it was declared optional; ADR 0011 now has to record the contradiction.
* **Write the ADR before the change, not after.** ADRs 0010 and 0011 were recorded retrospectively.
* **Make the quickstart a CI job.** Day 125's clean-room run found a missing database and a startup race that months of local runs never showed. A workflow that runs the README from scratch would have caught both.

## 7. Open items in one place

* Customer approval trusts `X-Customer-Id` / `X-Organization-Id` headers — customers can't log in yet (SECURITY_REVIEW, ADR 0010).
* Local infrastructure (Redis, RabbitMQ, Elasticsearch) runs without authentication (F8).
* Login limit sees the proxy's IP behind a reverse proxy unless forwarded headers are configured (F7).
* Readiness includes Redis; the notification service has no SQL retry; failed search returns 500 instead of 503 (ADR 0011).
* No automated test for the outbox retry or the Elasticsearch circuit breaker (verified live only).
* Migrations applied by hand; no migration job (ADR 0007).
* The notification service has no published image or Kubernetes manifest (ADR 0007).
* Integration events are held by two unchecked copies (ADR 0004).
* Cross-module references can dangle if a referenced row is ever deleted (ADR 0002/0003).
