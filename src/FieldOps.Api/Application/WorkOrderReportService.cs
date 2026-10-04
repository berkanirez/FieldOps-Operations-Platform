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
    public WorkOrderStatusReport GetStatusReport(int organizationId)
    {
        var cacheKey = $"workorders:report:{organizationId}";

        try
        {
            var cached = _redis.GetDatabase().StringGet(cacheKey);
            if (cached.HasValue)
            {
                return JsonSerializer.Deserialize<WorkOrderStatusReport>((string)cached!)!;
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis read failed for work order report of organization {OrganizationId}; computing from database", organizationId);
        }

        var workOrders = _workOrderDirectory.GetAll()
            .Where(w => w.OrganizationId == organizationId)
            .ToList();

        var report = new WorkOrderStatusReport(
            organizationId,
            Open: workOrders.Count(w => w.Status == WorkOrderStatus.Open),
            Assigned: workOrders.Count(w => w.Status == WorkOrderStatus.Assigned),
            InProgress: workOrders.Count(w => w.Status == WorkOrderStatus.InProgress),
            Completed: workOrders.Count(w => w.Status == WorkOrderStatus.Completed));

        try
        {
            _redis.GetDatabase().StringSet(cacheKey, JsonSerializer.Serialize(report), CacheDuration);
        }
        catch (RedisException ex)
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
