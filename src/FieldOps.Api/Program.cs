using System.Text;
using Elastic.Clients.Elasticsearch;
using FieldOps.Api.Application;
using FieldOps.Modules.AuditLogs;
using FieldOps.Modules.Customers;
using FieldOps.Modules.Employees;
using FieldOps.Modules.Organizations;
using FieldOps.Modules.WorkOrders;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Day 55: structured logging — JSON console output, with scopes turned on
// so CorrelationIdMiddleware's per-request scope actually appears in the
// output (IncludeScopes defaults to false; without it, BeginScope silently
// does nothing visible). JSON (not the plain-text simple console formatter)
// because it's the one built-in formatter that serializes a Dictionary-based
// scope into real, separate fields rather than just calling ToString() on it.
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

// Day 85: distributed tracing. AddAspNetCoreInstrumentation covers every
// inbound HTTP request automatically (no manual span needed, unlike
// RabbitMQ's publish/consume below); AddSource registers FieldOpsTracing's
// own ActivitySource so ITS manually-started spans are actually collected
// too — an ActivitySource nobody's "listening" to produces Activities that
// silently go nowhere. AddConsoleExporter is today's simplest possible
// "where do traces go" — printing them to the same console the JSON logs
// already go to, not a real trace backend like Jaeger/Zipkin.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("FieldOps.Api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(FieldOpsTracing.MessagingSourceName)
        .AddConsoleExporter())
    // Day 86: metrics — the AGGREGATE counterpart to tracing's per-request
    // story. AddAspNetCoreInstrumentation here (a metrics-specific overload,
    // unrelated to the tracing one above) gives request-count/duration
    // numbers for free; AddMeter registers FieldOpsMetrics's own Meter, the
    // same "nobody's listening = numbers go nowhere" reasoning as AddSource.
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(FieldOpsMetrics.MeterName)
        .AddConsoleExporter());

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Day 90: the browser blocks a cross-origin request (fieldops-web on
// localhost:4200 calling this API on a different port) unless the server
// explicitly allows that origin — CORS is a browser-enforced rule, not
// something curl/Postman (used by every earlier day's verification) ever
// had to satisfy. Scoped to exactly the Angular dev server's origin, not a
// wildcard, same "no broader than the real problem" spirit as Day 53's
// rate-limit partitioning.
var angularDevOrigin = builder.Configuration["Cors:AngularDevOrigin"] ?? "http://localhost:4200";
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy => policy
        .WithOrigins(angularDevOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Day 93: token ISSUANCE only today (AuthController.Login) — no endpoint is
// decorated with [Authorize] yet, and the existing X-Organization-Id/
// X-Employee-Id header mechanism (Day 40) stays exactly as it was. This
// registers the machinery to VALIDATE a bearer token when a later day
// actually starts requiring one; until then it has nothing to do.
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"] ?? "fieldops-dev-only-fallback-signing-key-do-not-use-in-production";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "FieldOps.Api";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtIssuer,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
        };
    });

// The host installs each module through its own extension method — it never
// names any module's internal concrete implementation class (or its
// DbContext) directly. Day 48/ADR 0003: every module now owns its own
// database; the host only ever hands over a connection string. The
// "get-connection-string-or-throw" logic is genuinely identical for all
// four calls (this week's "extract when it's really the same" rule), so it
// gets a tiny local function instead of being repeated four times.
string RequireConnectionString(string name) =>
    builder.Configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"Missing connection string: {name}");

builder.Services.AddOrganizationsModule(RequireConnectionString("FieldOpsOrganizationsDb"));
builder.Services.AddEmployeesModule(RequireConnectionString("FieldOpsEmployeesDb"));
builder.Services.AddWorkOrdersModule(RequireConnectionString("FieldOpsWorkOrdersDb"));
builder.Services.AddCustomersModule(RequireConnectionString("FieldOpsCustomersDb"));
builder.Services.AddAuditLogsModule(RequireConnectionString("FieldOpsAuditLogsDb"));

// Application-layer service: cross-module orchestration that belongs to the
// host (Day 34), not inside either module or directly inside a controller.
builder.Services.AddScoped<EmployeeApplicationService>();
builder.Services.AddScoped<WorkOrderAssignmentService>();

// Day 48 (Redis): registered as a Singleton factory — the actual TCP
// connection to Redis is only made the first time something resolves
// IConnectionMultiplexer (WorkOrderReportService, only when the report
// endpoint is actually called), not eagerly at startup. Existing tests that
// never call that endpoint never touch Redis at all.
//
// Day 52 fix (live-discovered via a CI failure, then a slow local repro):
// AbortOnConnectFail defaults to true, meaning Connect() blocks — for as
// long as the underlying OS socket connect takes, which was observed to be
// far longer than StackExchange.Redis's own ConnectTimeout on this Windows
// machine — before throwing when Redis is unreachable. With it set to
// false, Connect() returns immediately regardless of Redis's availability;
// the multiplexer keeps retrying in the background, and any command issued
// while disconnected fails fast with a RedisConnectionException instead of
// blocking. Combined with WorkOrderReportCacheWarmer's own try/catch, a
// Redis outage now degrades quickly and gracefully instead of blocking or
// crashing the whole host.
var redisConnectionString = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing configuration: Redis:ConnectionString");
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AbortOnConnectFail = false;

// Day 116 (found by profiling an 8.5 -> 37 minute test-suite slowdown): by
// default, while disconnected, StackExchange.Redis does not fail commands —
// it queues them in a backlog until the connection returns. A synchronous
// KeyDelete in WorkOrderReportService.InvalidateCache (called by Assign,
// Start, Complete, ...) was captured with dotnet-stack waiting there for
// over 11 minutes, so a Redis outage would hang those requests rather than
// let the existing catch blocks (Day 51/108/112) degrade gracefully. The
// cache is optional: fail fast and let the callers fall back.
redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddScoped<WorkOrderReportService>();
builder.Services.AddHostedService<WorkOrderReportCacheWarmer>();

// Day 54: idempotency — no per-request state of its own (just IConnectionMultiplexer,
// itself a Singleton), so this can safely be a Singleton too.
builder.Services.AddSingleton<IdempotencyService>();

// Day 63: AI provider abstraction — the same shape as Day 51's
// INotificationSender above. WorkOrderNoteSummaryService only ever depends
// on IAiProvider; a real provider (OpenAI/Anthropic) would later replace
// this one registration, with zero changes to the controller or the
// summary service itself. Both are Singletons: FakeAiProvider holds no
// state, and WorkOrderNoteSummaryService's only dependency is that same
// stateless Singleton.
builder.Services.AddSingleton<IAiProvider, FakeAiProvider>();
builder.Services.AddSingleton<WorkOrderNoteSummaryService>();

// Day 67: event-publishing abstraction — same shape again.
// WorkOrdersController only ever depends on IEventPublisher; a later
// change (a real exchange/routing-key design, a pooled connection) would
// replace this registration alone. RabbitMqEventPublisher takes only a
// hostname, not a connection, since it opens its own short-lived
// connection per publish (see its own comment for why).
var rabbitMqHostName = builder.Configuration["RabbitMq:HostName"] ?? "localhost";
builder.Services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(rabbitMqHostName));

// Day 79: Elasticsearch — a searchable copy of work orders, alongside (not
// instead of) SQL Server, which stays the single source of truth. The
// client is thread-safe and holds pooled connections, so it's a Singleton,
// the same shape as Day 48's IConnectionMultiplexer.
var elasticsearchUri = builder.Configuration["Elasticsearch:Uri"] ?? "http://localhost:9200";
builder.Services.AddSingleton(_ => new ElasticsearchClient(new Uri(elasticsearchUri)));
builder.Services.AddSingleton<IWorkOrderSearchIndex, ElasticsearchWorkOrderSearchIndex>();

// Day 83: ADR 0008's SOAP integration boundary, made real. Scoped, not
// Singleton — DataAccessBillingAmountSpeller creates a fresh generated SOAP
// client per call, so it holds no state worth sharing across requests (the
// same "no pooled connection yet" simplification RabbitMqEventPublisher
// made on Day 67).
builder.Services.AddScoped<IBillingAmountSpeller, DataAccessBillingAmountSpeller>();

// Day 68: the consumer side — a BackgroundService (same category as Day
// 50's WorkOrderReportCacheWarmer) that holds one long-lived RabbitMQ
// connection for the app's entire lifetime and reacts to
// WorkOrderCompletedEvent messages as they arrive, entirely independently
// of whatever HTTP request originally published one.
//
// Day 76: the notification-sending consumer (WorkOrderCompletedEventConsumer)
// was extracted into its own, genuinely separate FieldOps.NotificationService
// project, per ADR 0005 — it no longer lives here at all. This audit
// consumer is the one remaining consumer left inside FieldOps.Api; it's
// still a domain event by ADR 0004's own definition, since it never leaves
// this deployment.
builder.Services.AddHostedService<WorkOrderCompletedAuditConsumer>();

// Day 73: the Inbox pattern's storage — Scoped, since its only
// implementation depends on the Scoped IWorkOrderDirectory. The audit
// consumer above resolves this through a fresh scope per message (Day 50's
// pattern), never by holding it directly.
builder.Services.AddScoped<IInboxStore, WorkOrderInboxStore>();

// Day 71: the Outbox pattern's publishing side — WorkOrdersController.Complete
// no longer calls IEventPublisher directly at all; it only writes an outbox
// row (atomically, alongside the Status change, inside IWorkOrderDirectory.
// Complete). This background service is what actually reads unpublished
// rows and calls IEventPublisher, tolerating a RabbitMQ outage by simply
// retrying on its next tick instead of losing the event.
builder.Services.AddHostedService<OutboxPublisher>();

// Day 53: per-organization rate limiting — resource/performance isolation,
// the natural counterpart to Week 8's data isolation (a tenant can't see
// another tenant's data; now, a tenant can't degrade another tenant's
// performance either). Partitioned by X-Organization-Id, not by IP/user, so
// each organization gets its own independent "bucket" regardless of how
// many employees within it are making requests. A missing header falls
// into one shared "unknown" bucket — deliberately: a request with no
// organization identity is rejected by ValidateMembership anyway (400),
// long before it would ever reach a real database write, so a shared,
// generous bucket for that case is enough. This is .NET's own built-in
// middleware (Microsoft.AspNetCore.RateLimiting, since .NET 7) — no
// third-party package needed, the same idiomatic-native-first spirit as
// Day 50's BackgroundService over Hangfire/Quartz.
//
// PermitLimit/Window read from configuration, not hardcoded: the real demo
// value (small, so a human can trigger 429 in a few seconds) would break
// existing integration tests, which legitimately create far more than 5
// work orders for the same organization within far less than 10 seconds.
// FieldOpsApiFactory overrides these two settings to a much higher limit
// for the test environment — the same "override a config VALUE, not the
// business code" pattern Day 48 established for connection strings.
var rateLimitPermitLimit = builder.Configuration.GetValue("RateLimiting:PerOrganization:PermitLimit", 5);
var rateLimitWindowSeconds = builder.Configuration.GetValue("RateLimiting:PerOrganization:WindowSeconds", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("PerOrganization", httpContext =>
    {
        var organizationId = httpContext.Request.Headers["X-Organization-Id"].FirstOrDefault() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(organizationId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermitLimit,
            Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
            QueueLimit = 0
        });
    });
});

// Day 56: health checks — liveness ("is the process even running," no
// dependency touched, cheap and instant) vs. readiness ("can this instance
// actually do its job right now," which means its critical dependencies
// must be reachable). Both custom IHealthCheck implementations take only a
// connection string / the existing IConnectionMultiplexer — never a
// module's internal DbContext, preserving ADR 0001/0002's boundary.
// Only one of the five SQL Server databases (WorkOrders) is checked today —
// a deliberate scope narrowing, not an oversight; they all live on the same
// physical SQL Server instance in this demo setup.
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddTypeActivatedCheck<SqlServerHealthCheck>(
        "workorders-db",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: ["ready"],
        args: [RequireConnectionString("FieldOpsWorkOrdersDb")]);

var app = builder.Build();

// Day 81: unlike this repo's SQL Server migrations (always applied by hand,
// on purpose), ensuring the Elasticsearch index/mapping exists is safe to do
// automatically, every startup — it's fully idempotent (does nothing if the
// index is already there) and there's no equivalent "migration history" risk
// to guard against. In test hosts, IWorkOrderSearchIndex is overridden with
// a no-op (FieldOpsApiFactory), so this call does nothing there either.
//
// Wrapped in try/catch deliberately: the whole point of Day 80's work was
// that Elasticsearch being unreachable must never stop FieldOps.Api itself
// from working (Create/Complete degrade gracefully via the Outbox pattern).
// A startup crash here — the app refusing to even START because Elasticsearch
// happens to be down at that exact moment — would quietly reintroduce the
// exact hard dependency Day 80 just removed. If this fails, the index is
// simply left to Elasticsearch's own dynamic mapping (Day 79's fallback)
// once it does come back.
try
{
    using var startupScope = app.Services.CreateScope();
    var searchIndex = startupScope.ServiceProvider.GetRequiredService<IWorkOrderSearchIndex>();
    await searchIndex.EnsureIndexExistsAsync(CancellationToken.None);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not ensure the Elasticsearch search index exists at startup; it will fall back to dynamic mapping once reachable");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Day 55: registered before everything else so the correlation ID scope
// wraps the entire rest of the pipeline — every log line produced by any
// later middleware, controller, or service during this request inherits it.
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseHttpsRedirection();

// Day 90: must run before UseAuthorization/MapControllers so a preflight
// (OPTIONS) request is answered correctly before reaching any endpoint.
app.UseCors("AngularDev");

app.UseRateLimiter();

// Day 93: must run before UseAuthorization so a bearer token (once something
// actually requires one) is validated and its claims attached to the
// request before any authorization check runs.
app.UseAuthentication();

app.UseAuthorization();

// Day 58: the framework's default health check response is just the bare
// word "Healthy"/"Unhealthy" — no way to tell WHICH check failed. A JSON
// breakdown per check is what made today's Docker networking diagnosis
// (which dependency is actually unreachable from inside the container)
// observable at all, rather than a single opaque failure.
static Task WriteHealthCheckResponse(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";
    var payload = System.Text.Json.JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            description = e.Value.Description
        })
    });
    return context.Response.WriteAsync(payload);
}

// Day 56: /health/live never runs any check (Predicate: _ => false) — pure
// "is the process responding at all." /health/ready runs only the checks
// tagged "ready" — the real dependency checks.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthCheckResponse
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthCheckResponse
});

app.MapControllers();

app.Run();
