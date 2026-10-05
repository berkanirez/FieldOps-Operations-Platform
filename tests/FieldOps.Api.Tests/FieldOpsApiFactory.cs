using FieldOps.Api.Application;
using FieldOps.Modules.AuditLogs.Data;
using FieldOps.Modules.Customers.Data;
using FieldOps.Modules.Employees.Data;
using FieldOps.Modules.Organizations.Data;
using FieldOps.Modules.WorkOrders.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;

namespace FieldOps.Api.Tests;

// Day 48: mirrors StockPilot's StockPilotApiFactory (Day 28) — a real,
// disposable SQL Server spun up in a Docker container just for this test
// run. The real dev databases (localhost\SQLEXPRESS) are never touched by
// these tests at all. One container hosts all five modules' databases
// (distinct names, same instance) — the same "same instance, separate
// databases" shape ADR 0003 already uses for local dev, just disposable.
// No DbSeeder.Seed calls needed — each DbContext's own HasData (baked into
// its migration) seeds itself automatically when MigrateAsync runs.
public class FieldOpsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _dbContainer =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        await MigrateAsync(new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseSqlServer(ConnectionStringFor("FieldOpsOrganizations")).Options, o => new OrganizationsDbContext(o));
        await MigrateAsync(new DbContextOptionsBuilder<EmployeesDbContext>()
            .UseSqlServer(ConnectionStringFor("FieldOpsEmployees")).Options, o => new EmployeesDbContext(o));
        await MigrateAsync(new DbContextOptionsBuilder<WorkOrdersDbContext>()
            .UseSqlServer(ConnectionStringFor("FieldOpsWorkOrders")).Options, o => new WorkOrdersDbContext(o));
        await MigrateAsync(new DbContextOptionsBuilder<CustomersDbContext>()
            .UseSqlServer(ConnectionStringFor("FieldOpsCustomers")).Options, o => new CustomersDbContext(o));
        await MigrateAsync(new DbContextOptionsBuilder<AuditLogsDbContext>()
            .UseSqlServer(ConnectionStringFor("FieldOpsAuditLogs")).Options, o => new AuditLogsDbContext(o));
    }

    private static async Task MigrateAsync<TContext, TOptions>(TOptions options, Func<TOptions, TContext> create)
        where TContext : DbContext
    {
        await using var context = create(options);
        await context.Database.MigrateAsync();
    }

    // Same database name as the container's own default catalog, just
    // swapped out per module — SqlConnectionStringBuilder is the clean way
    // to change only the database name without hand-editing a raw string.
    private string ConnectionStringFor(string database)
    {
        var builder = new SqlConnectionStringBuilder(_dbContainer.GetConnectionString())
        {
            InitialCatalog = database
        };
        return builder.ConnectionString;
    }

    // Overrides the connection string CONFIGURATION VALUES that each
    // Add*Module call reads at startup — never touches any module's
    // DbContext or Ef*Directory by name here, for any of the five modules.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:FieldOpsOrganizationsDb", ConnectionStringFor("FieldOpsOrganizations"));
        builder.UseSetting("ConnectionStrings:FieldOpsEmployeesDb", ConnectionStringFor("FieldOpsEmployees"));
        builder.UseSetting("ConnectionStrings:FieldOpsWorkOrdersDb", ConnectionStringFor("FieldOpsWorkOrders"));
        builder.UseSetting("ConnectionStrings:FieldOpsCustomersDb", ConnectionStringFor("FieldOpsCustomers"));
        builder.UseSetting("ConnectionStrings:FieldOpsAuditLogsDb", ConnectionStringFor("FieldOpsAuditLogs"));

        // Day 53: the real, demo-sized rate limit (5 requests / 10 seconds
        // per organization) is far too strict for these tests — many
        // legitimately create more than 5 work orders for the same
        // organization within a single test class's run, which shares one
        // in-memory limiter instance for its whole lifetime. Overridden to
        // an effectively-unlimited value here so these authorization/
        // business-logic tests never fail for an unrelated reason; the
        // actual 429 threshold is verified live, not by an automated test.
        builder.UseSetting("RateLimiting:PerOrganization:PermitLimit", "100000");

        // Day 116: no Redis in this test environment (same stance as the
        // RabbitMQ/Elasticsearch no-ops below) — pinned to an address where
        // nothing listens, with short timeouts, so every run behaves the same
        // whether or not a developer happens to have Redis on localhost:6379.
        // Program.cs's BacklogPolicy.FailFast is what removed the 37-minute
        // suite (verified without this line); this only makes it deterministic.
        builder.UseSetting(
            "Redis:ConnectionString",
            "localhost:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200");

        // Day 67: real, live-caught test-suite slowdown — every test calling
        // Complete now tries to open a genuine RabbitMQ connection, which
        // has no chance of succeeding here (no RabbitMQ container in this
        // test environment) and only fails after its own connection
        // timeout. The full suite's run time roughly doubled before this
        // override was added. Same fix shape as Day 53's rate-limiting
        // override: swap out the real, slow, externally-dependent
        // implementation for a fast no-op, using the exact
        // ConfigureServices-runs-after-Program.cs mechanism Day 64 already
        // proved (there, to inject a failing fake; here, to inject a
        // free one).
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();

            // Day 79: same reasoning as NoOpEventPublisher above — every test
            // calling Create/Complete now also calls IWorkOrderSearchIndex,
            // which would otherwise try a genuine connection to Elasticsearch
            // (not present in this test environment) on every single call.
            services.AddSingleton<IWorkOrderSearchIndex, NoOpWorkOrderSearchIndex>();

            // Day 69: a second, real, live-caught test-suite regression — far
            // worse than Day 67's. WorkOrderCompletedAuditConsumer
            // (a BackgroundService, Day 69) holds a genuine, blocking
            // RabbitMQ connection attempt in its own ExecuteAsync.
            // Overriding IEventPublisher above does nothing for it — it
            // doesn't go through it at all. Unlike IEventPublisher, it's
            // registered as IHostedService, not a normal single-instance
            // service — a later AddSingleton doesn't "override" one of
            // these, ALL registered IHostedServices are started. So instead,
            // its registration is removed outright for the test host: its
            // own connectivity was already proven live, separately (Day
            // 69-74). Day 76: the notification-sending consumer this used to
            // also remove (WorkOrderCompletedEventConsumer) no longer lives
            // in FieldOps.Api at all — it was extracted into its own
            // FieldOps.NotificationService project, per ADR 0005, so this
            // test host never registers it in the first place.
            var hostedServicesToRemove = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(WorkOrderCompletedAuditConsumer))
                .ToList();
            foreach (var descriptor in hostedServicesToRemove)
            {
                services.Remove(descriptor);
            }
        });
    }

    // Deliberately does nothing and never fails — these tests care about
    // WorkOrdersController's own behavior, not about proving RabbitMQ
    // connectivity (Day 66/67 already proved that live, separately).
    private class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(TEvent domainEvent, string messageId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Deliberately does nothing and never fails — these tests care about
    // WorkOrdersController's own behavior, not about proving Elasticsearch
    // connectivity (that gets proven live, separately, same as RabbitMQ).
    private class NoOpWorkOrderSearchIndex : IWorkOrderSearchIndex
    {
        public Task IndexAsync(WorkOrderSearchDocument document, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkOrderSearchDocument>> SearchAsync(int organizationId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkOrderSearchDocument>>(Array.Empty<WorkOrderSearchDocument>());

        // Day 81: also a no-op — Program.cs calls EnsureIndexExistsAsync at
        // startup, including in this test host, and nothing here should
        // ever attempt a real Elasticsearch connection.
        public Task EnsureIndexExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RebuildOrganizationIndexAsync(int organizationId, IReadOnlyList<WorkOrderSearchDocument> documents, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // "new", not "override" — same reason as StockPilot's version (Day 28):
    // WebApplicationFactory's own IAsyncDisposable.DisposeAsync() returns
    // ValueTask, while xUnit's IAsyncLifetime requires one returning Task.
    public new async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await ((IAsyncDisposable)this).DisposeAsync();
    }
}
