# Interview Questions — C# and ASP.NET Core

Common junior/junior+ .NET interview questions, answered from this repository. Each answer has three parts:

* **Answer:** the short definition, one or two sentences you should be able to say without notes.
* **In FieldOps:** where the idea appears in this code, so the answer is backed by experience rather than recall.
* **Pitfall:** a common mistake, preferably one that actually happened in this project.

Where the project has no real example, the answer says so instead of inventing one. File references point to `src/FieldOps.Api` unless stated otherwise.

---

## C#

### 1. Value types versus reference types; `struct` versus `class`

**Answer:** A value-type variable holds the data itself and is copied on assignment; a reference-type variable holds a reference to an object on the heap, so two variables can point to the same object. `struct` is a value type, `class` a reference type. Use a `struct` only for small, immutable values where copying is cheap.

**In FieldOps:** the code uses `int`, `decimal`, `DateTime`, `bool` and enums such as `WorkOrderStatus` (value types) and classes and records for everything else. There is no custom `struct` — none was needed.

**Pitfall:** "value types live on the stack" is a simplification interviewers like to probe: a value-type field inside a class lives on the heap with its object. What defines a value type is copy semantics, not location.

### 2. What is a `record`, and why use it for DTOs and events?

**Answer:** A `record` is a reference type with value-based equality, a compiler-generated `ToString`, and concise positional syntax; with positional parameters its properties are init-only, so it is immutable by default. `with` creates a modified copy.

**In FieldOps:** every request and response DTO is a positional record — `Models/CreateWorkOrderRequest.cs`, `Models/WorkOrderDto.cs`, `Models/LoginResponse.cs` — as are `Application/WorkOrderCompletedEvent.cs` and `Application/JwtSettings.cs`. A DTO or event is data, not behaviour, and must not change after it is created.

**Pitfall:** value equality compares all properties; a record holding a mutable collection compares the collection by reference, so "immutable record" is only as immutable as its members.

### 3. Nullable reference types

**Answer:** With `<Nullable>enable</Nullable>`, reference types are non-nullable by default and `string?` marks one that may be null; the compiler warns when you might dereference null. It is a compile-time analysis, not a runtime check.

**In FieldOps:** enabled in every project (`FieldOps.Api.csproj`). `ClaimsPrincipalExtensions.GetOrganizationId()` returns `int?` because a token may lack the claim, and every caller handles `null` (returns 401). Required configuration uses `?? throw new InvalidOperationException("Missing configuration: ...")` in `Program.cs`, so the app fails at startup instead of with a null reference later. The build has 0 warnings.

**Pitfall:** the `!` (null-forgiving) operator silences the warning without making the value non-null. Use it only after a check the compiler cannot see, such as `organizationId!.Value` after `ValidateMembershipAsync` has already rejected `null`.

### 4. `async`/`await` and `Task`; what is thread-pool starvation?

**Answer:** `await` releases the current thread while an I/O operation is in progress and resumes when it completes, so a thread is not blocked waiting for the database or network. ASP.NET Core serves requests from the thread pool; if requests block threads (synchronous I/O, `.Result`, `.Wait()`), the pool runs out of threads under load and new requests queue: thread-pool starvation.

**In FieldOps:** Day 118 made the hot paths async end to end (controllers, application services, module directories, Redis). The k6 test at 50 virtual users went from 92 to 190 req/s, and list p95 from 547 ms to 31 ms; a search confirmed no `.Result`, `.Wait()` or `GetAwaiter().GetResult()` remained.

**Pitfall:** async does not make a single request faster; it lets the server handle more concurrent requests with the same threads. And "sync over async" (`.Result` on a task) brings back the blocking, and can deadlock where a synchronization context exists.

### 5. `IEnumerable<T>` versus `IQueryable<T>`

**Answer:** `IQueryable<T>` builds an expression tree that the provider (EF Core) translates to SQL; operators applied to it run in the database. `IEnumerable<T>` runs LINQ in memory over objects already loaded. Once a query becomes `IEnumerable` (for example after `ToList()` or `AsEnumerable()`), every later `Where` runs in C#.

**In FieldOps:** Day 113's bug. `GetAll()` loaded the whole `WorkOrders` table and controllers filtered by organization in memory, so the SQL had no `WHERE` and other tenants' rows were pulled into API memory. Moving the `Where` before materialization (`GetByOrganization`) made it SQL: list time 15 times faster for a small tenant, and the cross-tenant data stopped being loaded.

**Pitfall:** returning `IEnumerable<T>` from a method looks harmless, but the caller can no longer push filters to the database.

### 6. Deferred execution in LINQ

**Answer:** A LINQ query does not run when it is defined; it runs when it is enumerated (`foreach`, `ToList`, `Count`, `First`). With EF Core, that is when the SQL is sent.

**In FieldOps:** `Modules.WorkOrders/Data/EfWorkOrderDirectory.cs` composes `Where` → `OrderBy` → `Skip` → `Take` for pagination and only then calls `ToListAsync`; `GetStatusCountsAsync` composes `Where` → `GroupBy` → `Select` and ends with `ToDictionaryAsync`, so SQL Server returns at most four rows.

**Pitfall:** enumerating a query twice runs it twice. And a method group passed to `Select` (`.Select(ToSummary)`) cannot be translated to SQL; the project hit this and materialized first, then mapped.

### 7. `IDisposable` and `using`

**Answer:** `IDisposable` releases unmanaged resources deterministically; `using` (or `using var`) calls `Dispose` when the scope ends, even if an exception is thrown. `IAsyncDisposable` with `await using` is the async version.

**In FieldOps:** `OutboxPublisher` and `EventConsumerBase` create a DI scope per unit of work with `using var scope = _scopeFactory.CreateScope();`. `EfWorkOrderDirectory` uses `await using var transaction = await ...BeginTransactionAsync(...)`. `CorrelationIdMiddleware` uses `using (_logger.BeginScope(...))`, and tracing uses `using var activity = ...`.

**Pitfall:** not disposing a scope keeps its scoped services, such as a `DbContext` and its connection, alive.

### 8. Extension methods

**Answer:** A static method in a static class whose first parameter is marked `this`, so it can be called as if it were an instance method of that type. It adds behaviour without modifying or inheriting from the type.

**In FieldOps:** `Application/ClaimsPrincipalExtensions.cs` adds `User.GetOrganizationId()` and `User.GetEmployeeId()`, so the claim names live in one place. Every module exposes `services.AddOrganizationsModule(connectionString)` and similar — the standard ASP.NET Core way to let a library register itself without the host knowing its internal types.

**Pitfall:** extension methods are resolved at compile time by namespace; a missing `using` makes them silently unavailable.

### 9. Pattern matching and exception filters

**Answer:** Pattern matching tests a value against a shape (`is null`, type patterns, `or`/`and`, property patterns). In a `catch`, a type pattern or a `when` filter decides whether the handler applies at all; an exception that does not match keeps propagating.

**In FieldOps:** `WorkOrderReportService` catches `RedisException or RedisTimeoutException`. That exact line is the fix for a real bug: `RedisTimeoutException` derives from `TimeoutException`, not from `RedisException`, so the original `catch (RedisException)` missed it and a load test showed 11% errors with Redis down (Day 112).

**Pitfall:** assuming an exception hierarchy instead of checking it, and the opposite mistake — catching `Exception` everywhere, which hides your own bugs.

### 10. Access modifiers; why `internal` matters

**Answer:** `public` is visible everywhere, `internal` only inside the same assembly, `protected` to derived classes, `private` inside the type. `InternalsVisibleTo` can open internals to a specific assembly, usually a test project.

**In FieldOps:** this is how module boundaries are enforced (ADR 0001). Entities and `DbContext` classes are `internal` to their module project; the host can use only the public interface (`IWorkOrderDirectory`) and DTOs (`WorkOrderSummary`). Trying to instantiate an internal class from `FieldOps.Api` is a compile error (CS0122), so a boundary violation fails the build.

**Pitfall:** a public method cannot expose an internal type (CS0050). That error is what forced the separate public DTO in the first place.

---

## ASP.NET Core

### 11. The middleware pipeline — why does order matter?

**Answer:** Each middleware receives the request, may act, and calls the next one; the response travels back through them in reverse. Order is the order of `app.Use...` calls, and it decides what each middleware can see and what it protects.

**In FieldOps:** `Program.cs` lines 387–413: `UseExceptionHandler` first (so it catches exceptions from everything after it), then `CorrelationIdMiddleware`, HTTPS redirection, CORS, `UseAuthentication`, `UseAuthorization`, and `UseRateLimiter` last.

**Pitfall:** Day 121–122. The rate limiter partitions by the organization claim, but it ran before authentication, so the claim did not exist yet and every caller fell into one shared "anonymous" bucket. Moving `UseRateLimiter` after `UseAuthorization` fixed it, and also means rejected requests never spend a tenant's quota.

### 12. Dependency injection lifetimes; what is a captive dependency?

**Answer:** Singleton: one instance for the application's lifetime. Scoped: one instance per request (or per created scope). Transient: a new instance every time it is resolved. A captive dependency is a longer-lived service holding a shorter-lived one, such as a singleton holding a scoped `DbContext`: the scoped object outlives its scope and is shared across requests.

**In FieldOps:** `DbContext`s and services that use them are scoped (`WorkOrderReportService`, `WorkOrderAssignmentService`). `IConnectionMultiplexer` is a singleton because it is designed to be shared. Background services are singletons, so `OutboxPublisher` and `EventConsumerBase` take `IServiceScopeFactory` and create a scope per tick or per message instead of injecting scoped services directly — the standard way to avoid a captive dependency.

**Pitfall:** `DbContext` is not thread-safe; a captured one shared by concurrent requests produces intermittent errors. In Development, ASP.NET Core's scope validation throws when a singleton resolves a scoped service from the root provider, which catches many of these early.

### 13. Model binding and validation

**Answer:** Model binding fills action parameters from the route, query string, headers and body. With `[ApiController]`, an invalid model (by data annotations) automatically returns `400` with a `ValidationProblemDetails` body before the action runs.

**In FieldOps:** request records carry annotations, e.g. `CreateWorkOrderRequest([Required, StringLength(200)] string Title, int? CustomerId)` and `CreateEmployeeRequest` with `StringLength(128, MinimumLength = 12)` for the password. Rules annotations cannot express are added by hand: `WorkOrdersController.GetAll` rejects `pageSize` outside 1–100 with `ModelState.AddModelError` and `ValidationProblem`.

**Pitfall:** validation of shape is not authorization. A perfectly valid `employeeId` can still belong to another tenant — that check lives in the application code.

### 14. Authentication versus authorization; what is a fallback policy?

**Answer:** Authentication establishes who the caller is (here: validating a JWT and building a `ClaimsPrincipal`); authorization decides what that caller may do. A fallback policy applies to every endpoint that has no authorization metadata of its own.

**In FieldOps:** `Program.cs` sets `FallbackPolicy = RequireAuthenticatedUser`, so every endpoint requires a token unless it is explicitly `[AllowAnonymous]` (login, health checks, OpenAPI, customer approval). Role and ownership rules are checked in the controller helpers against the database (`ValidateIsAdminAsync`, `ValidateOwnershipAsync`). See ADR 0010.

**Pitfall:** relying on `[Authorize]` per controller means a forgotten attribute silently exposes an endpoint. With a fallback policy, the risky action — `[AllowAnonymous]` — is the visible one.

### 15. How is a JWT validated?

**Answer:** The server checks the signature with its key, then the claims it was told to trust: issuer, audience, and lifetime (`exp`, with a small clock skew). Only then are the claims used. A JWT is signed, not encrypted: anyone can read its contents.

**In FieldOps:** `Program.cs` sets `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` and `ValidateIssuerSigningKey` to `true`; the token lasts one hour (`AuthController`). The app refuses to start without `Jwt:SigningKey` — the earlier fallback key in source code meant anyone could mint valid tokens (SECURITY_REVIEW F4).

**Pitfall:** putting secrets in a JWT, or accepting a token without checking its signature. Also: a stateless token cannot be revoked before it expires; FieldOps re-reads the employee from the database so a deleted employee is rejected.

### 16. Configuration and secrets

**Answer:** ASP.NET Core layers configuration sources: `appsettings.json`, `appsettings.{Environment}.json`, user secrets in Development, environment variables, command-line arguments — later sources override earlier ones. `Section__Key` in an environment variable maps to `Section:Key`.

**In FieldOps:** connection strings and the signing key come from environment variables in Docker Compose (from a gitignored `.env`), from a Kubernetes Secret created on the command line, and from Azure configuration. Limits such as `RateLimiting:Login:PermitLimit` are read with defaults. Missing required settings stop the app at startup.

**Pitfall:** a "convenient" default for a secret. It is exactly what turned the signing key into a public value (F4).

### 17. Background work: `IHostedService` and `BackgroundService`

**Answer:** A hosted service runs alongside the web host, started and stopped with it. `BackgroundService` gives you one method, `ExecuteAsync(CancellationToken)`, for a long-running loop; the token signals shutdown.

**In FieldOps:** `OutboxPublisher` (publishes unpublished outbox rows on a `PeriodicTimer`), `WorkOrderReportCacheWarmer`, and the RabbitMQ consumers (`EventConsumerBase` subclasses).

**Pitfall:** an unhandled exception escaping `ExecuteAsync` stops the service, and by default the host as well. `OutboxPublisher` wraps every tick in `try/catch` and logs, so one bad tick cannot stop publishing — a lesson from Day 52.

### 18. Error handling and ProblemDetails

**Answer:** ProblemDetails (RFC 9457, formerly 7807) is a standard JSON shape for HTTP errors: `type`, `title`, `status`, `detail`. `AddProblemDetails()` plus `UseExceptionHandler()` turn unhandled exceptions into a 500 ProblemDetails response without leaking a stack trace.

**In FieldOps:** both are configured (`Program.cs` lines 65 and 387); controllers return `Problem(...)`, `ValidationProblem(...)` and `NotFound()`; an integration test (`ErrorHandlingIntegrationTests`) checks that an unhandled exception produces a ProblemDetails body without internals (F10).

**Pitfall:** returning `200` with an error message in the body, or a bare 500 with a stack trace. Another open example: a failed search currently surfaces as 500, where `503` would be more truthful (ADR 0011).

### 19. Rate limiting — what is the partition key, and why take it from claims?

**Answer:** The rate limiting middleware counts requests per partition (a key derived from the request) using an algorithm such as fixed window, sliding window, token bucket or concurrency; when the limit is reached it returns `429`.

**In FieldOps:** two named policies in `Program.cs`. `PerOrganization` partitions by the validated organization claim, so one tenant cannot exhaust another's quota. `Login` partitions by client IP address (five attempts per minute), to slow password guessing. Rejections are logged as security events.

**Pitfall:** partitioning by a value the client controls — the original version used an `X-Organization-Id` header, which anyone could change to get a fresh bucket (F5). Behind a reverse proxy the IP address is the proxy's, unless forwarded headers are configured for trusted proxies only (F7 caveat).

### 20. `CancellationToken` — where should it go?

**Answer:** ASP.NET Core supplies a token that is cancelled when the client disconnects (`HttpContext.RequestAborted`, or a `CancellationToken` action parameter). Pass it through every asynchronous I/O call so abandoned requests stop using the database and network.

**In FieldOps:** controller actions take a `CancellationToken` and pass it down to the module interfaces (`IWorkOrderDirectory.GetPageByOrganizationAsync(..., CancellationToken)`) and from there to EF Core's `ToListAsync(cancellationToken)`. Background services use the shutdown token of `ExecuteAsync`.

**Pitfall:** accepting the token and not passing it on, which is the same as not having it. And cancelling inside a write after the commit point — cancellation belongs on I/O boundaries, not halfway through business logic.

### 21. Health checks: liveness versus readiness

**Answer:** Liveness answers "is the process alive?" — if not, restart it. Readiness answers "can this instance serve traffic right now?" — if not, stop sending it requests, but don't restart it. They should check different things.

**In FieldOps:** `/health/live` checks nothing external; `/health/ready` checks SQL Server and Redis (`Program.cs`, health-check registration and lines 441–449). Kubernetes uses them as liveness and readiness probes (`k8s/fieldops-api-deployment.yaml`).

**Pitfall:** putting dependencies in liveness, so a database outage makes Kubernetes restart every Pod in a loop. FieldOps avoids that — but ADR 0011 records that Redis in readiness contradicts the "Redis is optional" decision: a Redis outage would remove every API Pod from traffic. Reporting it as `Degraded` (which maps to `200` by default) would fix that.
