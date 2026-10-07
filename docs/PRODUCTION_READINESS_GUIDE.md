# Production Readiness Guide

A checklist for building production-grade backend systems (with a frontend, a database and a cloud deployment). It is distilled from building FieldOps end to end: a .NET 10 modular monolith with SQL Server, Redis, RabbitMQ, Elasticsearch, an Angular frontend, Docker, Kubernetes, GitHub Actions and Azure. Every rule below either prevented or fixed a real problem in that project. Where a rule exists because of a specific failure, the failure is named.

## How to use this guide (instructions for an AI assistant)

- Treat every item as a requirement unless the project explicitly decides otherwise. Record such decisions as a known limitation or an ADR. Never skip an item silently.
- Never claim something works without running it. "Done" means built, tested, and verified against the real running system, with the evidence shown.
- Work in small vertical slices: one behaviour, end to end, tested and verified, before starting the next.
- When a problem appears, form a hypothesis, then **measure** before fixing. In FieldOps, two "obvious" hypotheses turned out wrong once measured: a memory-pressure theory for a slow test suite, and a "cache makes it faster" assumption.
- Fix bugs test-first: write a test that reproduces the bug, watch it fail for the right reason, then fix it.
- Prefer the framework's built-in, idiomatic solution over a new dependency. When adding a package, state what problem it solves.
- Do not introduce abstractions, patterns or layers without a concrete problem they solve today.
- Look before deleting or overwriting anything: count the affected rows or files first.
- Keep secrets out of source control, logs, terminal output and AI prompts.

---

## 1. Engineering principles

- **Measure, don't assume.** Use load tests, query plans, logical reads, thread-pool statistics, logs and metrics. Compare against a recorded baseline taken under identical conditions.
- **Red before green, for the right reason.** A test that passes before the fix exists is a false green. Example: a 404 test passed because the route didn't exist yet. Assert the reason, for example "the owner gets 200 for the same id, so the 404 comes from the tenant check".
- **Fix the root cause, not the symptom.** A slow suite was "fixed" by Redis fail-fast behaviour, not by sharing containers. The profiling showed where the time actually went.
- **A fix in a shared method changes every caller.** Review all callers. A cache fallback that was correct for user requests silently turned a background warmer into wasted database load.
- **Verify the change in the real environment.** "The deploy command succeeded" is not "the new version serves traffic". Verify with real requests.
- **Document honestly.** Keep "Known limitations" in the README, and record each intentional simplification next to the production alternative.
- **Small, reviewed, reversible steps.** Never rewrite history or delete data without explicit permission.

## 2. Architecture and code organization

- **Start with a modular monolith**, not microservices. Split a service out only for a concrete reason, such as independent scaling, a different deployment cadence or failure isolation, and record the reason in an ADR.
- **Module boundaries:**
  - Each module exposes a small public interface and keeps its implementation `internal`: the DbContext, entities and repository-like classes.
  - The host wires modules through one extension method per module (`AddWorkOrdersModule(connectionString)`) and never names internal types.
  - Modules don't reference each other. Cross-module rules live in application services in the host.
- **Database per module** (logical separation) when modules must stay independent. Accept the trade-off explicitly: no cross-module foreign keys or joins.
- **Application services** hold cross-module orchestration and business rules. Keep them free of HTTP types so they are unit-testable. Controllers only translate HTTP to and from these calls.
- **Interfaces only where they earn their place:** a real seam such as a module boundary, an external dependency (search index, event publisher, AI provider) or a test double. Don't create an interface for every class.
- **No generic repository over EF Core.** The DbContext already is a unit of work and repository. Expose intention-revealing methods instead, such as `GetPageByOrganizationAsync` and `GetStatusCountsAsync`.
- **Don't expose fetch-everything methods.** A `GetAll()` that callers filter in memory will be misused. Remove it once purpose-specific queries exist.
- **Explicit request/response DTOs**, separate from entities and from module summary types. Map manually at first, so the data flow stays visible.
- **Never put business rules in controllers.**
- **Keep sensitive data out of general-purpose types.** A password hash must not be part of the summary type returned by lists.
- **Configuration:**
  - Read configuration once at startup into typed settings, validate it, and fail fast when critical values are missing.
  - Never hard-code a fallback secret. A public fallback signing key let anyone forge tokens.
- **Record significant decisions in ADRs:** context, decision, consequences, alternatives.

## 3. API design

- **REST with correct status codes:**

  | Code | Meaning |
  |---|---|
  | 200 | Read or update succeeded |
  | 201 | Created (with the resource) |
  | 204 | Success with no body |
  | 400 | Validation error |
  | 401 | Not authenticated |
  | 403 | Authenticated but not allowed |
  | 404 | Not found, or not yours |
  | 409 | Conflict |
  | 429 | Rate limited |
  | 503 | Dependency unavailable |

- **ProblemDetails (RFC 9110) for every error**, including unhandled exceptions: `AddProblemDetails` + `UseExceptionHandler` first in the pipeline. Include a `traceId`. Never include exception messages or stack traces.
- **Validation:** data annotations on request DTOs; `[ApiController]` returns `ValidationProblem` automatically. Add explicit checks for query parameters, such as page ranges.
- **Pagination on every list:**
  - deterministic ordering (`ORDER BY` a unique key) before `Skip`/`Take`;
  - default page size plus a hard maximum (for example 50 / 100);
  - keyset (cursor) pagination for deep pages at scale.
- **A single-resource endpoint for every resource the UI shows.** Don't make clients download a list to find one item.
- **Another tenant's resource returns 404, not 403**, so ids can't be enumerated (IDOR).
- **Idempotency keys for create operations** that clients may retry. Note that they do not cover server-side retries inside one request.
- **`CancellationToken` on every async action**, passed all the way to the database and HTTP calls.
- **Identity never comes from the request body or client-supplied headers.** Derive tenant and user from the validated token.
- **Keep response shapes stable.** When narrowing data, keep the contract; for example, return a one-element array rather than changing the type.
- **OpenAPI** for discovery in development; anonymous access only where intended.

## 4. Database and EF Core

### Schema and migrations

- **Every schema change is a migration**, reviewed before applying. Read EF's "possible data loss" warnings; often only `Down` is destructive.
- **Apply migrations as idempotent SQL scripts** (`dotnet ef migrations script --idempotent`) in a controlled step, not automatically at app startup: multiple replicas would race. Prove idempotency by running the script twice.
- **Never remove a migration that has been shared.** Add a new one. Removing is fine only before it was committed or applied anywhere else.
- **Seed data must be deterministic.** Use constants (for example a pre-computed password hash), never values generated during model building such as random salts or `DateTime.Now`.
- **Keep every environment's database at the latest migration.** A local database once lagged two months of migrations behind.

### Queries

- **Filter, sort, count and page in SQL**, not in memory. Everything before `ToList`/`ToListAsync` runs on `IQueryable` and becomes SQL; everything after runs in memory. Check the generated SQL in the logs.
- **Project only the columns you need.** Beware method groups in `Select` (`Select(ToSummary)`): they bind to `Enumerable.Select` and run in memory.
- **Aggregate in the database** (`GROUP BY` + `COUNT`) instead of loading rows to count them.
- **Use parameterized queries only.** EF Core does this by default; avoid raw SQL APIs, or use their parameterized forms.

### Indexes

- **Add indexes for measured query patterns.** Every index slows writes and costs storage.
- **Measure with `SET STATISTICS IO` (logical reads) and the actual execution plan**, not just timing. Logical reads are stable across runs; timings are not.
- **Beware parameter sniffing on skewed data.** One cached plan can be right for a small tenant and catastrophic for a large one (8 vs 150,089 reads). Remedies:
  - a covering index for aggregate queries;
  - pagination, to bound the row count;
  - `OPTION (RECOMPILE)` or Query Store plan forcing where justified.
- **Column order in composite indexes matters:** the leading column must be the one you filter on.

### Transactions and resilience

- **Enable transient-fault retries** (`EnableRetryOnFailure`) for cloud and serverless databases.
- **Run user-started transactions inside the execution strategy** (`CreateExecutionStrategy().ExecuteAsync(...)`), and call `ChangeTracker.Clear()` at the start of each attempt so a retry doesn't insert duplicates.
- Know the limit: if a commit succeeds but the acknowledgement is lost, a retry can duplicate it. Covering that needs a database-checkable idempotency key.
- **Use the outbox pattern** when a database change must reliably trigger a message: write the change and the outbox rows in one transaction, and publish them afterwards.

### Configuration

- Connection strings come from secrets and environment variables, never from tracked files.
- Never enable sensitive data logging outside local debugging.
- Run the app with a least-privilege database user, not the admin login.

## 5. Async I/O and concurrency

- **Async end to end for I/O:** EF (`ToListAsync`, `SaveChangesAsync`), Redis (`StringGetAsync`), HTTP. Synchronous I/O blocks thread-pool threads and causes **thread-pool starvation** under load.
  - Symptom: `WORKER Busy` far above `Min` in timeout messages, p95 in seconds.
  - Async took p95 from 547 ms to 31 ms at 50 virtual users.
- **Async improves scalability, not single-request latency.** Expect no difference at low load.
- **Never block on async code** (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`). Scan for them.
- **Reads and writes share one thread pool.** Synchronous writes slowed already-async reads 30×.
- **`out` parameters can't exist on async methods.** Return a tuple or a small result type; separate fetching from a shared, pure decision method so the sync and async paths can't drift apart.
- **Decide deliberately which work honours cancellation.** Side effects after a successful commit (audit records, cache invalidation) should not be cancelled by a disconnecting client.
- **Background services:**
  - Use `IServiceScopeFactory` to create a scope per unit of work.
  - Never let an exception escape `ExecuteAsync`; the default behaviour stops the whole host.
  - Treat `OperationCanceledException` during shutdown as normal, not as a failure.
  - Use `PeriodicTimer` for schedules.

## 6. Resilience and fault tolerance

- **Classify every dependency as required or optional.** Optional dependencies (cache, search, notifications) must degrade gracefully, never take the core request down.
- **Catch the exact exception types of the failure you tolerate, and verify the hierarchy.**
  - `RedisTimeoutException` derives from `TimeoutException`, not `RedisException`; catching only `RedisException` let timeouts through and produced 11% errors under load.
  - Don't use bare `catch (Exception)` in request paths, because it hides programming errors. Use filtered catches (`catch (Exception ex) when (ex is A or B)`).
- **Timeouts on every external call**, sized so a dead dependency costs milliseconds, not seconds.
- **Fail fast when a dependency is disconnected.** StackExchange.Redis's default backlog queued commands and hung requests for 11 minutes; `BacklogPolicy.FailFast` lets the existing fallbacks run. (For Redis holding authoritative data, weigh this differently.)
- **Retries:** only for transient errors, bounded in count, exponential backoff with jitter. Never retry non-idempotent operations blindly.
- **Circuit breakers** for repeatedly failing dependencies, so failures don't pile up and the system recovers on its own.
- **Guard background work on dependency health.** Skip a cache warmer's ticks while the cache is down; it would otherwise compute and discard results. Log only on state transitions, not on every tick.
- **Side effects after a committed change must not fail the request:** cache invalidation, audit logging, notifications. Log and continue; repair asynchronously when needed.
- **Messaging:**
  - outbox for publishing;
  - an inbox / idempotent consumers keyed by message id;
  - bounded retries, then a dead-letter queue;
  - consumers survive broker outages and reconnect.
- **Health checks:**
  - **liveness** = the process responds; touches no dependencies, so failures don't trigger restart loops;
  - **readiness** = critical dependencies are reachable, so the instance is taken out of rotation instead of restarted;
  - return a per-check JSON breakdown.
- **Graceful startup:** a missing optional dependency at startup must not prevent the app from starting. For example, wrap index creation in a try/catch.

## 7. Caching

- **Cache-aside** with a TTL plus explicit invalidation on writes that affect the cached value.
- **The cache is an accelerator, never a dependency.** Every read and write path must work with the cache down, including timeouts.
- **Key per tenant** (`workorders:report:{organizationId}`); never share keys across tenants.
- **Measure the cache's actual value.** After a covering index, the report cache no longer reduced latency (p95 39.9 vs 39.1 ms) but cut database queries from 12,483 to 18. Its value was capacity, not speed. Decide on that basis.
- **Guard warmers and refreshers** on cache availability.

## 8. Security

### Authentication

- **Secure by default:** a fallback authorization policy (`RequireAuthenticatedUser`) so every endpoint requires a token. Opt out explicitly with `[AllowAnonymous]` only where needed: login, health checks, OpenAPI in development.
- **Identity comes from validated token claims, never from client headers, query strings or bodies.** In FieldOps, headers-only identity let anyone act as any tenant's admin. Keep "forged header is ignored" as a regression test.
- **JWT validation:** issuer, audience, lifetime and signing key. Short lifetimes.
- **The signing key comes from a secret store.** The app refuses to start without it. Never use a fallback key in code.
- **Re-check the database for the token's subject:** a valid token can outlive a deleted or moved user (401 / 403).
- **Production systems should delegate identity** to a provider (Entra ID, Auth0, Keycloak). They give you MFA, password reset, lockout and refresh-token rotation.

### Passwords (if you manage them yourself)

- Hash with a slow, salted algorithm: ASP.NET Core Identity's `PasswordHasher` (PBKDF2), Argon2 or bcrypt. Never a fast hash such as SHA-256, and never encryption.
- Store only the hash. Keep it out of DTOs, lists and logs; hash in the application layer.
- **Uniform failure:** the same status, body and timing for "unknown user" and "wrong password". Equalize timing by verifying against a dummy hash when the user doesn't exist.
- Minimum length (12+). Rate limit login attempts.

### Authorization and tenant isolation

- **Tenant isolation everywhere**, including metadata. A list endpoint once exposed every tenant's name. Filter by the token's tenant in SQL.
- **Role checks and ownership checks** (for example, only the assignee may complete a work order).
- **Horizontal privilege escalation:** never compare against a client-supplied tenant; use the token's tenant by construction.
- **404 for other tenants' resources.**
- **UI role hiding is a convenience, not security.** Enforce on the server.

### Abuse protection

- **Rate limit per authenticated identity** (tenant claim), with the limiter placed **after** authentication. Otherwise headers can be rewritten for fresh buckets, and rejected requests spend another tenant's quota.
- **Rate limit login per client address.** Behind a reverse proxy, configure the forwarded-headers middleware with the known proxies first.
- **Raise limits only for tests**, through configuration, never in code.

### Input, output and transport

- Validate all input; parameterized SQL only. Prove it: an injection string must be stored verbatim.
- Errors never leak internals (see API design).
- CORS: an explicit origin list, never `*`, for credentialed APIs.
- HTTPS everywhere; HSTS in production.

### Secrets and supply chain

- Secrets live in environment variables, Kubernetes Secrets (base64 is not encryption, so never commit Secret manifests), or a cloud secret store / Key Vault. Use `.env` files locally, and keep them gitignored with a committed `.env.example`.
- Never print secrets to the terminal, and never put them in logs or prompts.
- **Scan dependencies regularly:** `dotnet list package --vulnerable --include-transitive`, `npm audit --omit=dev`.
- **Pin dependency versions** (no floating `4.10.*`) for reproducible builds.
- Give CI tokens least privilege (for example `contents: read`; `packages: write` only for the publishing job).
- Secure infrastructure: Redis password/ACL, non-default broker credentials, search-engine security enabled, management UIs not publicly exposed.

### Security operations

- **Log security events** under a dedicated category with a structured event field: login failed/succeeded, rate-limit rejections, authorization failures. Never log passwords or tokens. Alert on spikes.
- **Self-review against the OWASP Top 10**, with live evidence and a prioritized remediation plan. Re-run the original probes after each fix and keep them as regression tests.

## 9. Observability

- **Structured logging** (JSON in containers) with message templates (`{OrganizationId}`), not string concatenation.
- **A correlation ID** per request: generated or accepted at the edge, added to the log scope, returned in a response header.
- **Use log levels deliberately:**

  | Level | Use for |
  |---|---|
  | Information | Normal events |
  | Warning | Degraded but working (cache unavailable, fallback used) |
  | Error | Failed operations |

- **Log state transitions, not repetitions.** "Redis unavailable, pausing" once, not every 10 seconds.
- **Metrics:** request counts by status class, latency percentiles, replica counts, business counters (for example work orders completed).
- **Tracing** with OpenTelemetry for request flows across components.
- **Make logs queryable** (Log Analytics / KQL, or equivalent), and keep a few saved queries: errors by category, top messages, security events.
- **Don't suppress errors in diagnostics commands.** A `2>/dev/null` once hid a query syntax error, so the query only looked empty.
- **Alerts** on error rate, latency and security-event spikes.

## 10. Performance and load testing

- **Load test before production** with a scripted tool such as k6. Keep the scenarios in the repo and add thresholds (p95, error rate) so the tool exits non-zero and can gate a pipeline.
- **Report p50/p95/p99, not averages.** Averages hide the tail users feel.
- **Use realistic data volumes and skew** (one large tenant, several small ones) and realistic mixes (readers plus writers at once).
- **Compare against a baseline under identical conditions:** same data, same scenario, same duration. Reset created data between runs.
- **Warm up before measuring.** A first-request sample includes JIT and static initialization; it once showed 3.6 s against a steady state of about 150 ms.
- **Know the scenario's ceiling.** VUs × requests per iteration ÷ think time. Reaching it means the system absorbs the offered load; the bottleneck is now the scenario.
- **The load generator and the system under test should run on separate machines.** When they don't, read the results as relative.
- **Never load test shared or cloud environments** you don't own or pay for per use.
- **Profiling toolkit:**
  - `dotnet-stack` for live thread stacks;
  - `docker stats` for container CPU and memory;
  - per-test durations (`--logger "console;verbosity=normal"`);
  - EF command logs for SQL and timing;
  - thread-pool statistics in timeout messages.
- **Track test-suite duration.** A suite that jumps from 8.5 to 37 minutes signals a real bug. Here it was the same Redis backlog hang that would have hung production requests.

## 11. Testing

- **Unit tests** for business rules and services without HTTP: fast, deterministic, hand-written fakes for small interfaces.
- **Integration tests** for HTTP, the database and infrastructure behaviour: `WebApplicationFactory` plus a real database in a container (Testcontainers).
  - Never mock EF Core just to make a test pass.
  - Apply real migrations in tests (`MigrateAsync`), so every test run also tests the migrations.
- **Authenticate tests the way clients do:** log in through the real endpoint and send the bearer token. No test-only auth shortcut.
- **Make external dependencies deterministic in tests:** point optional services at unreachable addresses with short timeouts, or replace them with no-op implementations. Results must not depend on whether a developer has Redis running.
- **Every bug gets a regression test that reproduces it first.** This includes security probes: forged headers, wrong-key tokens, enumeration.
- **Keep tests independent of shared state and execution order.** Derive expectations from the data present, not from assumed ids.
- **Send valid payloads in authorization tests**, so a 403 test fails for the authorization reason, not for model validation.
- **Fakes for large interfaces** without a mocking library: `DispatchProxy`. Throw `NotSupportedException` for unexpected calls so a test can't pass silently.
- **Test what is logged** with a capturing logger provider, for example "the password never appears in any log line".
- **Frontend:** service tests with `HttpTestingController` (exact URL, method, body, and error paths: 404 vs 500), and component tests for form validation and navigation.
- **Run the full suite before calling anything done**, and keep it fast.

## 12. Frontend (Angular as the example)

- **All HTTP lives in services**, never in components; one place for URLs and error mapping.
- **Use relative API URLs** (`/api/...`). A hard-coded `http://localhost:port` points every visitor's browser at their own machine. In development, use a dev-server proxy; in production, a reverse proxy (nginx) forwards `/api`.
- **An HTTP interceptor attaches the bearer token.** Components never handle tokens.
- **Use reactive state signals for data that changes the view**, especially in zoneless change detection: plain fields don't trigger re-rendering.
- **Reactive forms with validators.** Don't call the API while the form is invalid. Error messages must not reveal more than the API does ("check the employee id and password").
- **Map expected errors explicitly** (404 → "not found") and let unexpected ones surface. Never show "not found" for a server error.
- **Hiding UI by role is UX only.** The server must enforce every rule.
- **Token storage is a trade-off.** Memory is the safest against XSS but is lost on refresh. localStorage persists but is exposed to XSS. HttpOnly cookies need CSRF protection. Choose explicitly.
- **Route guards** for authenticated areas; redirect to login on 401.
- **No hard-coded demo identities or headers** once real authentication exists.
- **Accessibility basics:** labels bound to inputs, `type="password"`, `autocomplete` attributes.
- **Run frontend tests and a production build in CI.**

## 13. Containers and orchestration

- **Multi-stage Dockerfiles:** build in an SDK/Node image, run in a minimal runtime/nginx image. Use a `.dockerignore` for `node_modules`, `bin`, `obj`, `dist`. Order layers for caching (restore before copying sources).
- **Configuration from the environment** (12-factor). For nginx, use env-substituted templates. Give each image a safe default so an unconfigured container still starts.
- **SPA hosting:** nginx `try_files $uri $uri/ /index.html` so deep links work.
- **Reverse-proxy headers:** forward `Host` correctly for platforms that route by host (`proxy_set_header Host $proxy_host`), plus `X-Forwarded-*`.
- **Kubernetes:**
  - a Deployment with at least 2 replicas for stateless components (self-healing);
  - a Service for stable addressing;
  - an Ingress for one entry point;
  - ConfigMap for settings and Secret for secrets, created by command or from a secret store, never committed;
  - readiness and liveness probes (see Resilience);
  - resource requests (needed for autoscaling);
  - a HorizontalPodAutoscaler.
- **A component that runs background workers** needs care with replica counts (for example, singletons such as publishers).
- **Image tags are immutable commit SHAs**, plus `latest` for convenience only. Rollback points at a SHA.

## 14. CI/CD

- **On every push:** restore, build, and run all test suites (backend and frontend), plus a production build of the frontend.
- **Validate workflow files** before pushing (`actionlint`) and shell scripts (`shellcheck`).
- **Publish images only from the main branch, and only after all test jobs pass** (`needs:`). Tag with the commit SHA.
- **Least-privilege tokens:** per-run `GITHUB_TOKEN`, no stored registry passwords.
- **Never interpolate user input into shell commands** (`${{ inputs.x }}` inside `run:`); pass it through `env:` (script injection).
- **Smoke test after every deployment** with an automated script that:
  - exits non-zero on failure;
  - retries for cold starts;
  - checks response bodies, not only status codes (an SPA fallback returns 200 for everything);
  - checks that anonymous API calls are rejected (401);
  - logs in and exercises the core endpoints.
- **Deployment should be a pipeline step** (OIDC federated credentials to the cloud, no stored secrets), followed by the smoke test, with automatic rollback on failure.
- **Reproducible builds:** pinned package versions and lock files (`npm ci`).

## 15. Cloud deployment and operations

- **Cost guardrails first:** a budget with alerts, a single resource group per environment, spending limits on trial accounts, scale-to-zero where acceptable. Know what bills while idle.
- **Use internal-only ingress for APIs** that only the frontend calls. Verify that the API is unreachable from the internet.
- **Secrets as platform secrets** (Container Apps secrets referenced via `secretref:`, Key Vault).
- **Migrations as a separate, idempotent step** before or alongside the deploy.
- **Deploy = point the service at a new immutable image.** **Rollback = point it at the previous SHA.** Practise rollback before you need it.
- **Verify every deploy and rollback with real requests.** The command reporting success is not proof that traffic switched; the old revision kept serving for seconds.
- **Expect cold starts** with scale-to-zero (API start plus serverless database resume took 20+ seconds); size client and smoke-test timeouts accordingly. Each serverless database resumes independently.
- **Logs and metrics in the platform:** query them after deploys; correlate 5xx spikes with revisions.
- **Tear down what you don't use**, and verify that the resource list is empty.

## 16. Documentation

- **The README is the front door:** what the project is, how to run it (a single `docker compose up` where possible), demo identities (local-only credentials clearly marked), an architecture diagram, how to test, and honest known limitations.
- **Architecture diagrams as text** (Mermaid in Markdown), versioned with the code.
- **ADRs** for decisions with trade-offs.
- **A security review document** with findings, evidence, status and remediation plan.
- **API examples** that actually work against the current version. Re-check them after auth changes; a stale example showed requests that now return 401.
- **Runbooks:** how to deploy, roll back, rotate secrets, and diagnose common failures.
- **Comments explain why, not what.** Remove comments that state something no longer true.

## 17. Definition of Done (per change)

- [ ] Behaviour works against the real running system, verified with actual requests.
- [ ] Builds with 0 warnings; the full backend and frontend test suites pass.
- [ ] A new or updated test covers the change; bug fixes have a test that failed first, for the right reason.
- [ ] Failure cases considered: dependency down, timeout, invalid input, unauthorized, another tenant, concurrency and retries.
- [ ] No secrets in code, logs or output; the security implications reviewed (authn, authz, tenant isolation, input validation).
- [ ] Performance-sensitive paths measured against a baseline when relevant.
- [ ] Documentation updated: README, known limitations, ADR or security review, and examples still correct.
- [ ] Temporary data and processes cleaned up; containers stopped.
- [ ] Commit message explains what and why.

## 18. Pitfalls that actually happened (and the rule they produced)

1. **Hard-coded `localhost` API URL in the frontend.** It worked only on the developer's machine. Rule: use relative URLs plus a reverse proxy.
2. **Headers-only identity.** Anyone could impersonate any tenant's admin. Rule: identity from validated token claims only.
3. **Public fallback JWT signing key**, with none configured in production, so tokens were forgeable. Rule: fail fast without a secret-store key.
4. **Credential-less login and user enumeration** (404 vs 200). Rule: password hashes plus a uniform 401 with equalized timing.
5. **Rate limiter keyed on a raw header** (`"01"` bypass) and running before authentication (cross-tenant quota exhaustion). Rule: key on the token claim, place the limiter after authentication.
6. **Removing the identity headers made header-less requests share one "unknown" rate-limit bucket.** Rule: when changing what identifies a caller, re-check everything that keyed on the old identifier.
7. **Organization list exposed every tenant.** Rule: tenant isolation includes metadata.
8. **Report endpoint returned 500 without Redis.** Rule: optional dependencies degrade gracefully.
9. **`catch (RedisException)` missed `RedisTimeoutException`.** Rule: verify exception hierarchies; test the timeout path under load.
10. **Redis backlog queued commands while disconnected**, hanging requests for 11 minutes (and the test suite for 37). Rule: fail fast plus timeouts on optional dependencies.
11. **A correct fallback turned a background warmer into wasted database work.** Rule: review every caller of a changed method; guard background work on dependency health.
12. **Fetch-all queries filtered in memory**, reading 50,002 rows to show 2. Rule: filter and aggregate in SQL; remove fetch-all methods.
13. **Parameter sniffing made a single-column index 90× worse** for a large tenant. Rule: measure plans and logical reads for skewed data; use covering indexes and pagination.
14. **Synchronous I/O starved the thread pool under load.** Rule: async end to end, then re-measure.
15. **`EnableRetryOnFailure` broke a user-started transaction.** Rule: wrap the whole transaction in the execution strategy and clear the change tracker per attempt.
16. **Serverless database cold starts produced 500s.** Rule: transient retries, plus generous timeouts in smoke tests.
17. **"Deploy succeeded" while the old revision still served traffic.** Rule: verify with real requests.
18. **A 404 test passed before the endpoint existed.** Rule: assert the reason, not just the status.
19. **Tests sharing a database assumed which ids existed.** Rule: derive expectations from the data present.
20. **A suppressed error output hid a KQL syntax error** (`last` is reserved). Rule: never hide errors while diagnosing.
21. **PowerShell's culture-sensitive `-match`** failed on the Turkish "I" in a variable name. Rule: use culture-invariant / case-sensitive comparisons in scripts.
22. **Git Bash rewrote `/bin/sh` into a Windows path** inside `kubectl` arguments. Rule: `MSYS_NO_PATHCONV=1` for container commands from Git Bash.
23. **Bash scripts break with CRLF line endings.** Rule: `.gitattributes` with `*.sh text eol=lf`.
24. **A first measurement included warm-up and looked like a security flaw** (3.6 s vs 0.48 s). Rule: warm up, then repeat interleaved samples before concluding.
25. **A stale README example** returned 401 after the auth change. Rule: re-verify docs whenever behaviour changes.
26. **A local database lagged migrations behind** for weeks unnoticed. Rule: check `migrations list` for pending migrations in every environment you run.

## 19. Open items in FieldOps (for reference)

Intentionally left open and documented:

- Customer authentication (approval still trusts a customer header).
- Authentication for local infrastructure (Redis, RabbitMQ, Elasticsearch).
- A few write endpoints and minor controllers still synchronous.
- Forwarded-headers configuration behind a proxy.
- Token refresh and revocation.
- Account management (password policy, reset, lockout, MFA).
- Deployments run by hand rather than by a pipeline step.
- No alerting rules.
