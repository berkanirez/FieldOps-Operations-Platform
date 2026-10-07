# Interview Questions — SQL, EF Core and Distributed Systems

Same format as [csharp-aspnetcore.md](csharp-aspnetcore.md): **Answer** (what to say), **In FieldOps** (where it is in this repository), **Pitfall** (a common mistake, ideally one that happened here). Numbers are local measurements from the learning log, not production traffic.

---

## SQL Server and EF Core

### 1. Clustered, nonclustered and covering indexes

**Answer:** The clustered index *is* the table, its rows stored in key order (usually the primary key). A nonclustered index is a separate sorted structure holding its key columns and a pointer to the row. A covering index contains every column a query needs, so SQL Server never has to go back to the table.

**In FieldOps:** `WorkOrders` has a composite nonclustered index on `(OrganizationId, Status)` (migration `AddWorkOrdersOrganizationIdStatusIndex`, Day 114). It covers the status report query — filter by organization, group by status — so the largest tenant's report dropped from 1,674 to 114 page reads.

**Pitfall:** adding an index without measuring. The first attempt, a single-column index on `OrganizationId`, made one tenant's query far worse (next question).

### 2. Reading an execution plan: seek, scan, key lookup

**Answer:** A *seek* navigates the index tree to the matching rows; a *scan* reads the whole index or table; a *key lookup* fetches the remaining columns from the clustered index for each row a nonclustered index found. Lookups are cheap for a few rows and very expensive for many. Measure with `SET STATISTICS IO ON` (logical reads — 8 KB pages, independent of load) as well as time.

**In FieldOps:** Day 114 ran the exact EF Core queries through `sp_executesql` with `STATISTICS IO` and `STATISTICS PROFILE`. With no index: a clustered index scan, 1,674 reads even for 2 rows. With the single-column index: seek + key lookup, 8 reads for the small tenant.

**Pitfall:** judging a plan by its shape ("it's a seek, so it's good") instead of its reads.

### 3. Parameter sniffing

**Answer:** SQL Server compiles a plan for a parameterized query using the parameter value of the first execution and caches it. If data is skewed, a plan that is ideal for one value can be terrible for another.

**In FieldOps:** organization 1 had 2 work orders, organization 2 had 30,000+. With the single-column index, whichever tenant ran first decided the plan: compiled for the small tenant (seek + lookup), the large tenant needed 150,089 reads — 90 times worse than scanning the table. The fix was to make lookups unnecessary: the covering `(OrganizationId, Status)` index gives the same seek plan that is right for both tenants (2–3 and 114 reads, in either execution order).

**Pitfall:** the usual "fixes" (`OPTION (RECOMPILE)`, clearing the plan cache) hide the problem. Look for a plan that is right for every value. For the list query, which needs every column, the real fix was pagination, not a wider index.

### 4. The N+1 query problem

**Answer:** Loading a list with one query, then issuing one more query per item (often through lazy loading or a loop calling the database): N+1 round trips instead of one or two. Detect it in the SQL log; fix it with `Include`, a projection, or one batched query.

**In FieldOps:** not encountered as such. The modules have no navigation properties across modules (separate databases, ADR 0003) and lazy loading is not enabled, so nothing loads related rows behind your back. The closest real problem was the opposite shape, Day 113: one query that loaded too much (every tenant's rows) and filtered in memory.

**Pitfall:** a loop that calls a repository or another module's interface per item. With database-per-module, a cross-module list ("work orders with employee names") would need one batched lookup by ids, not one call per row.

### 5. Pagination: OFFSET/FETCH versus keyset

**Answer:** OFFSET/FETCH (`Skip`/`Take`) is simple and supports "jump to page N", but SQL Server still reads and discards the skipped rows, so deep pages get slower, and concurrent inserts can shift items between pages. Keyset (seek) pagination uses "rows after the last key I saw" (`WHERE Id > @lastId ORDER BY Id`): constant cost and stable, but no random page access.

**In FieldOps:** `GET /api/workorders?page=&pageSize=` uses OFFSET/FETCH with a stable `ORDER BY` and a maximum page size of 100 (`WorkOrdersController.MaxPageSize`); larger values return 400. Integration-tested in `WorkOrdersPaginationIntegrationTests`. Keyset was not needed at this data size.

**Pitfall:** paginating without an `ORDER BY` — the order is then undefined and pages can overlap or skip rows.

### 6. Transactions, isolation levels and optimistic concurrency

**Answer:** A transaction makes several changes atomic. The isolation level (SQL Server's default is READ COMMITTED) decides which anomalies concurrent transactions can see. Optimistic concurrency avoids locks: each row carries a version (`rowversion`), the update includes it in its `WHERE`, and if another writer changed the row first, zero rows match and EF Core throws `DbUpdateConcurrencyException`.

**In FieldOps:** completing a work order writes the state change and the outbox row in one transaction (`EfWorkOrderDirectory`, around line 91). Optimistic concurrency lives in StockPilot: `Product.RowVersion`, and `ProductsController` maps `DbUpdateConcurrencyException` to a 409 conflict.

**Pitfall:** read-modify-write without either a transaction or a concurrency token: two requests read the same state and the last writer silently wins.

### 7. Migrations in production

**Answer:** EF Core migrations version the schema in code. In production, generate a reviewed SQL script (`dotnet ef migrations script --idempotent`) and apply it as a deployment step or migration job, rather than calling `Database.Migrate()` from every app instance at startup.

**In FieldOps:** each module and the notification service have their own migration history. Azure used idempotent scripts, safe to re-run because applied migrations are skipped. The README applies all six with `dotnet ef database update`. On Day 114 an uncommitted migration was removed locally; the rule recorded then: a migration that has been shared is never removed, a new one is added.

**Pitfall:** forgetting a database entirely. On Day 125 the README never created the notification service's database, and only that service's logs showed it. Migrations are still applied by hand; a migration job is an open item.

### 8. Change tracking, `AsNoTracking` and `ChangeTracker.Clear`

**Answer:** EF Core tracks every entity it loads so `SaveChanges` can work out what changed. For read-only queries, `AsNoTracking()` skips that work. `ChangeTracker.Clear()` detaches everything, for example before retrying a unit of work.

**In FieldOps:** StockPilot's `EfProductStore` uses `AsNoTracking()` for display-only reads and explains why one method deliberately does not. FieldOps's read paths mostly project into DTOs (`Select`), which EF Core does not track anyway. `EfWorkOrderDirectory` calls `ChangeTracker.Clear()` at the start of the retried transaction block, so a retry does not reuse half-applied state from the failed attempt.

**Pitfall:** loading entities with tracking for a large read-only list wastes memory and CPU.

### 9. `EnableRetryOnFailure` and execution strategies

**Answer:** `EnableRetryOnFailure` makes EF Core retry operations that fail with transient SQL errors (dropped connections, failovers, throttling). With retries enabled, a user-started transaction must run inside `Database.CreateExecutionStrategy().ExecuteAsync(...)`, so the whole transaction is retried as a unit.

**In FieldOps:** found live on Azure SQL (Day 111): transient faults failed requests that a retry would have saved. Every module now enables it, and the outbox transaction runs inside the execution strategy. Without it, EF Core refuses the user transaction outright with an `InvalidOperationException` — proven during the change.

**Pitfall:** retrying non-idempotent work outside a transaction, which can apply it twice. The notification service still lacks retries (ADR 0011, open).

### 10. Should you mock EF Core in tests?

**Answer:** Mocking `DbContext`/`DbSet`, or using the in-memory provider, tests your mock rather than SQL: translation, constraints, transactions and indexes all behave differently. Integration tests against the real database engine catch what matters; unit tests are for business rules without I/O.

**In FieldOps:** integration tests run against a real SQL Server in a Testcontainers container through `WebApplicationFactory` (`FieldOpsApiFactory`), logging in through the real endpoint. Unit tests cover rules without HTTP (`EmployeeApplicationServiceTests`, `WorkOrderNoteSummaryServiceTests`). Redis failures are simulated with a `DispatchProxy` fake of `IConnectionMultiplexer` (`WorkOrderReportServiceTests`), because the point there is the failure path, not Redis itself.

**Pitfall:** a green test that proves nothing. Day 123's 404 test passed before the route even existed; an extra assertion (the owner gets 200) was added so the test could actually fail.

---

## Distributed systems

### 11. Monolith, modular monolith, microservices

**Answer:** A monolith deploys as one unit. A modular monolith is still one deployment but has enforced internal boundaries. Microservices are independently deployable services that communicate over the network. Microservices buy independent deployment and scaling, at the cost of network failures, eventual consistency and operational overhead.

**In FieldOps:** a modular monolith (ADR 0001) with one service extracted where it paid off: the notification service, which only reacts to events (ADR 0005). ADR 0007 records that one Compose file running both is not yet independent deployment.

**Pitfall:** the "distributed monolith" — services sharing a database or calling each other synchronously for everything, so they can only be deployed together.

### 12. Database per service and data consistency

**Answer:** Each service (or module) owns its data, and others access it only through its API. That rules out cross-database foreign keys and transactions; consistency between services becomes eventual, maintained with events, outbox/inbox and compensation.

**In FieldOps:** five module databases plus the notification service's own (ADR 0003, 0005). The host validates cross-module references at write time (ADR 0002); nothing repairs a reference if the target is later deleted — a gap that is accepted and documented, since no feature deletes organizations or employees.

**Pitfall:** claiming database-per-service and then joining across the databases "just for a report".

### 13. REST versus messaging

**Answer:** Use a synchronous call when the caller needs the answer before it can continue; use asynchronous messaging to announce a fact that others may react to later, especially when the receiver may be unavailable.

**In FieldOps:** ADR 0006. Approving a work order is REST (the customer needs the result); "work order completed" is a message (the API should not wait for notifications or care whether the notification service is up).

**Pitfall:** request/reply over a queue to avoid HTTP, adding complexity HTTP already solves.

### 14. The outbox pattern and the dual-write problem

**Answer:** Writing to the database and publishing to a broker are two separate systems; whichever goes second can fail, leaving them inconsistent (the dual-write problem). The outbox writes the event to a table in the same transaction as the business change; a separate process publishes outbox rows and marks them sent.

**In FieldOps:** `Complete` writes the status change and an outbox row in one transaction; `OutboxPublisher` publishes every few seconds and retries until it succeeds. On Day 88, RabbitMQ and Elasticsearch were stopped together, a work order was completed (200 OK), and every outbox row was published within about ten seconds of restarting them. The same outbox feeds the Elasticsearch index.

**Pitfall:** the outbox gives at-least-once delivery, not exactly-once — the publisher can crash after publishing and before marking the row. Consumers must be idempotent (next question).

### 15. At-least-once delivery, idempotency and the inbox pattern

**Answer:** Brokers like RabbitMQ deliver a message at least once; redelivery happens after failures or missing acknowledgements. An idempotent consumer produces the same result however many times it receives a message. The inbox pattern stores the ids of processed messages and skips repeats.

**In FieldOps:** consumers record `(ConsumerName, MessageId)` in a `ProcessedMessages` table and skip messages already there (`IInboxStore`, `EventConsumerBase`). For HTTP, `POST /api/workorders` accepts an `Idempotency-Key` header and replays the original response for a retried request.

**Pitfall:** "the broker guarantees exactly-once" — usually not, end to end. Idempotency in the consumer is what makes duplicates harmless.

### 16. Dead-letter queues

**Answer:** A dead-letter queue holds messages that could not be processed after a number of attempts, so one poison message does not block the queue or retry forever, and someone can inspect it later.

**In FieldOps:** a consumer gives a message three delivery attempts (`EventConsumerBase.MaxDeliveryAttempts = 3`, attempts tracked in `FailedMessageAttempts`), then moves it to its dead-letter queue (`EventQueueNaming.DeadLetterQueueNameFor`) and logs it.

**Pitfall:** a dead-letter queue nobody monitors is a silent data-loss queue. FieldOps logs each dead-lettered message but has no alert on it.

### 17. Timeouts, retries with backoff, circuit breakers

**Answer:** A timeout bounds how long you wait. A retry with exponential backoff (and jitter) handles transient failures without hammering a struggling dependency. A circuit breaker stops calling a dependency that keeps failing, failing fast for a while and then letting a trial call through.

**In FieldOps:** Elasticsearch calls go through a Polly pipeline — a circuit breaker around a 2-second timeout (`ElasticsearchWorkOrderSearchIndex`, Day 87). RabbitMQ consumers reconnect with exponential backoff capped at 30 seconds. SQL Server uses EF Core retries. Redis uses fail-fast.

**Pitfall:** retries without a timeout or a cap multiply load during an outage. And the Redis lesson (Day 116): the client's default backlog queued commands during an outage, so nothing failed and the fallback code never ran — the absence of a fast failure is also a failure mode.

### 18. Cache-aside and invalidation; what is a cache really for?

**Answer:** Cache-aside: read from the cache; on a miss, read from the database and store the result with an expiry. On writes, invalidate (delete) the cached entry. The hard parts are invalidation and deciding whether the cache is worth its complexity.

**In FieldOps:** the per-organization status report is cached in Redis; assign, start, complete and other writes delete the key; a warmer pre-fills it. Day 123 measured its value: after the Day 114 index, the cache no longer bought latency (p95 about 40 ms either way), but it cut database queries by 99.9% (18 instead of 12,483 in a 30-second run). Its value is shielding the database, not speed.

**Pitfall:** assuming a cache makes things faster without measuring, and caching tenant data under a key that doesn't include the tenant.

### 19. Observability: logs, metrics, traces, correlation IDs

**Answer:** Logs record individual events; metrics aggregate numbers over time (rates, latencies, counts); traces follow one request across services as a tree of spans. A correlation ID ties together all logs for one request.

**In FieldOps:** structured JSON logging with a correlation ID from `CorrelationIdMiddleware` (`X-Correlation-Id`, added to every log line in the request scope); OpenTelemetry tracing with the trace context carried through RabbitMQ message headers, so a trace continues into the notification service (`FieldOpsTracing`, `EventConsumerBase`); custom metrics, including completed work orders and outbox publish lag (`FieldOpsMetrics`).

**Pitfall:** high-cardinality labels (user ids, raw URLs) on metrics, which explodes storage. Per-request detail belongs in logs and traces.

### 20. Graceful degradation; fail open versus fail closed

**Answer:** Graceful degradation means an optional dependency failing reduces functionality instead of taking the whole system down. When a protective check itself can't run, the system can *fail open* (skip the check and continue) or *fail closed* (refuse); the right choice depends on the cost of each mistake.

**In FieldOps:** ADR 0011. SQL Server is essential; Redis, RabbitMQ and Elasticsearch degrade (no cache, delayed events, no search). Idempotency fails open when Redis is down — a duplicate work order is possible — because blocking all creates would be worse for field work; for payments, fail closed would be right.

**Pitfall:** treating "degrade gracefully" as "catch everything". The ADR also lists where the code doesn't comply yet, such as Redis in readiness.

### 21. Domain events versus integration events; contracts and versioning

**Answer:** A domain event is consumed inside the same deployment, so its shape can change freely with its consumers. An integration event crosses a deployment boundary and is a contract: consumers deploy on their own schedule, so changes must be backward compatible (add optional fields; never rename or remove), or versioned.

**In FieldOps:** ADR 0004 classified `WorkOrderCompletedEvent` as a domain event and named the trigger for reclassification; it fired on Day 76 when the notification service was extracted. Each side keeps its own copy of the record and they agree only on the JSON, so nothing checks compatibility — the build no longer catches a drift (ADR 0004/0005, Later Developments).

**Pitfall:** sharing a "contracts" assembly too early couples deployments again; never sharing anything means agreement is unchecked. The middle ground (a versioned, published schema or package) becomes worth it with a second consumer service.
