using FieldOps.Modules.Organizations;
using StackExchange.Redis;

namespace FieldOps.Api.Application;

// Day 50: proactive cache warming. WorkOrderReportService.GetStatusReport is
// already cache-aside (Day 48) — a hit does nothing, a miss recomputes and
// re-caches. This background service just calls that same method on a
// schedule, for every organization, so a real user request almost never has
// to pay the cold-cache cost itself.
//
// A BackgroundService is registered as a Singleton (it lives for the whole
// app's lifetime), but WorkOrderReportService and IOrganizationDirectory are
// Scoped — a Singleton is not allowed to hold a Scoped dependency directly
// (proven live: constructor-injecting them here made the app fail to even
// start, with "Cannot consume scoped service ... from singleton
// IHostedService"). IServiceScopeFactory is itself a Singleton-safe service
// whose only job is to hand out a fresh scope on demand — one new scope per
// tick, disposed right after, exactly like ASP.NET Core creates one per HTTP
// request under the hood.
public class WorkOrderReportCacheWarmer : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkOrderReportCacheWarmer> _logger;
    private bool _warmingPaused;

    public WorkOrderReportCacheWarmer(IServiceScopeFactory scopeFactory, ILogger<WorkOrderReportCacheWarmer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Live-discovered bug (via a CI failure, not staged): the
            // per-organization try/catch below only protected
            // GetStatusReport itself — it never protected
            // GetRequiredService<WorkOrderReportService>() a few lines
            // above, which is exactly where IConnectionMultiplexer's lazy
            // Redis connection is actually attempted. When Redis was
            // unreachable (no Redis service in GitHub Actions' CI runner),
            // that threw OUTSIDE any try/catch, escaped ExecuteAsync
            // entirely, and — because BackgroundServiceExceptionBehavior
            // defaults to StopHost — took down the ENTIRE application host,
            // failing every unrelated test (and, in real production, every
            // unrelated request) sharing that same process. A single
            // background tick's failure must never be allowed to escape
            // ExecuteAsync at all, for any reason.
            try
            {
                await WarmAllOrganizationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Day 118: the host is shutting down mid-tick — not a failure.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Work order report cache warming tick failed");
            }
        }
    }

    // Day 118: async, using GetStatusReportAsync — the synchronous version no
    // longer exists (Day 117's thread-pool starvation finding).
    private async Task WarmAllOrganizationsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        // Day 112 (found in Azure logs, Day 109): warming exists only to fill
        // Redis. Since Day 108 the report falls back to the database when
        // Redis is down, so without this guard every tick computed every
        // organization's report and then failed to cache it — wasted database
        // work. IsConnected reads in-memory state (no network call), and the
        // multiplexer keeps reconnecting in the background, so warming resumes
        // on its own. Logged only on a transition, not on every 10s tick.
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        if (!redis.IsConnected)
        {
            if (!_warmingPaused)
            {
                _warmingPaused = true;
                _logger.LogWarning("Redis is unavailable; pausing work order report cache warming until it reconnects");
            }
            return;
        }

        if (_warmingPaused)
        {
            _warmingPaused = false;
            _logger.LogInformation("Redis is reachable again; resuming work order report cache warming");
        }

        var organizationDirectory = scope.ServiceProvider.GetRequiredService<IOrganizationDirectory>();
        var workOrderReportService = scope.ServiceProvider.GetRequiredService<WorkOrderReportService>();

        foreach (var organization in organizationDirectory.GetAll())
        {
            try
            {
                await workOrderReportService.GetStatusReportAsync(organization.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One organization's failure (e.g. a transient DB blip)
                // must not stop the rest of this same tick's organizations
                // from being warmed — the outer try/catch above is the
                // last-resort safety net; this inner one keeps failures
                // scoped as narrowly as possible when the failure really is
                // per-organization.
                _logger.LogWarning(ex, "Failed to warm work order report cache for organization {OrganizationId}", organization.Id);
            }
        }
    }
}
