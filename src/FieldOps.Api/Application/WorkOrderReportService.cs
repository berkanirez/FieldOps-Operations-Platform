using System.Text.Json;
using FieldOps.Api.Models;
using FieldOps.Modules.WorkOrders;
using StackExchange.Redis;

namespace FieldOps.Api.Application;

// Day 48 (Redis, first step): cache-aside — check Redis first, compute and
// store on a miss, return on a hit. Deliberately TTL-only today (no active
// invalidation when a work order's status changes) — a real, documented
// limitation, not an oversight: an event-driven invalidation step is
// planned as a follow-up, once this basic read-through shape is proven live.
public class WorkOrderReportService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IWorkOrderDirectory _workOrderDirectory;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<WorkOrderReportService> _logger;

    public WorkOrderReportService(IWorkOrderDirectory workOrderDirectory, IConnectionMultiplexer redis, ILogger<WorkOrderReportService> logger)
    {
        _workOrderDirectory = workOrderDirectory;
        _redis = redis;
        _logger = logger;
    }

    // Day 108 fix: the cache is an accelerator, never a dependency. Both
    // Redis calls are guarded so an unreachable Redis (found live on Azure,
    // Day 107, where no Redis is deployed) degrades to "always compute from
    // the database" instead of a 500. Same principle as InvalidateCache below.
    //
    // Day 117 correction (found by the first load test: 11% of requests at 50
    // virtual users failed with an unhandled RedisTimeoutException): Day 108
    // caught only RedisException, assuming timeouts derive from it. They
    // don't — RedisTimeoutException derives from System.TimeoutException
    // (verified by reflection) — so both kinds are caught explicitly. Still
    // not a bare catch (Exception): a JSON or programming error must surface.
    //
    // Day 118: async end-to-end (Redis StringGetAsync/StringSetAsync, EF
    // ToDictionaryAsync) — Day 117's load test showed synchronous I/O here
    // starving the thread pool (WORKER Busy=42, Min=12).
    public async Task<WorkOrderStatusReport> GetStatusReportAsync(int organizationId, CancellationToken cancellationToken)
    {
        var cacheKey = $"workorders:report:{organizationId}";

        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                return JsonSerializer.Deserialize<WorkOrderStatusReport>((string)cached!)!;
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Redis read failed for work order report of organization {OrganizationId}; computing from database", organizationId);
        }

        // Day 113: counted by the database (GROUP BY) instead of loading every
        // work order and counting in memory.
        var counts = await _workOrderDirectory.GetStatusCountsAsync(organizationId, cancellationToken);

        var report = new WorkOrderStatusReport(
            organizationId,
            Open: counts.GetValueOrDefault(WorkOrderStatus.Open),
            Assigned: counts.GetValueOrDefault(WorkOrderStatus.Assigned),
            InProgress: counts.GetValueOrDefault(WorkOrderStatus.InProgress),
            Completed: counts.GetValueOrDefault(WorkOrderStatus.Completed));

        try
        {
            await _redis.GetDatabase().StringSetAsync(cacheKey, JsonSerializer.Serialize(report), CacheDuration);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Redis write failed for work order report of organization {OrganizationId}; returning uncached result", organizationId);
        }

        return report;
    }

    // Day 49: active invalidation — called by every controller action that
    // changes a work order's Status (the only thing this report counts).
    // Reassign/Approve never touch Status, so they never call this.
    //
    // Day 51 fix: by the time any caller reaches this point, the real
    // mutation already succeeded and was persisted — invalidating the cache
    // is a side effect, not the operation itself. A Redis blip here must
    // never turn an already-successful Complete/Assign/etc. into an error
    // response for the caller, the exact same principle Day 51 applied to
    // INotificationSender. Worst case: a stale cached report for up to the
    // remaining TTL, not a failed request.
    //
    // Day 119: async version for Create/Assign/Start/Complete — the mixed
    // load test showed the synchronous KeyDelete (UNLINK) timing out 350
    // times while blocking threads. Unassign/Reopen still use the sync one
    // until their conversion. No CancellationToken on purpose: the change it
    // invalidates for is already saved.
    public async Task InvalidateCacheAsync(int organizationId)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync($"workorders:report:{organizationId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate work order report cache for organization {OrganizationId}", organizationId);
        }
    }

    public void InvalidateCache(int organizationId)
    {
        try
        {
            var db = _redis.GetDatabase();
            db.KeyDelete($"workorders:report:{organizationId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate work order report cache for organization {OrganizationId}", organizationId);
        }
    }
}
