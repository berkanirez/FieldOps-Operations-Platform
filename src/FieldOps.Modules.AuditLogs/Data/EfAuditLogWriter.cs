using FieldOps.Modules.AuditLogs.Domain;
using Microsoft.Extensions.Logging;

namespace FieldOps.Modules.AuditLogs.Data;

// internal — the real implementation of IAuditLogWriter.
//
// Day 51's lesson applied here from day one, not retrofitted after a live
// failure this time: recording an audit entry is a side effect of an
// already-successful business operation (the work order was genuinely
// Assigned/Completed/Approved before this is ever called). A database blip
// while inserting this row must never turn that already-successful mutation
// into a failed HTTP response for the caller — so the try/catch lives here,
// inside the one place that does the risky I/O, not repeated at each of the
// controller's call sites.
internal class EfAuditLogWriter : IAuditLogWriter
{
    private readonly AuditLogsDbContext _dbContext;
    private readonly ILogger<EfAuditLogWriter> _logger;

    public EfAuditLogWriter(AuditLogsDbContext dbContext, ILogger<EfAuditLogWriter> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public void Record(int organizationId, int workOrderId, string action, string actorType, int actorId)
    {
        try
        {
            _dbContext.AuditLogEntries.Add(new AuditLogEntry(organizationId, workOrderId, action, actorType, actorId, DateTime.UtcNow));
            _dbContext.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record audit log entry for work order {WorkOrderId} ({Action})", workOrderId, action);
        }
    }

    // Day 119: same failure isolation, awaited instead of blocking.
    public async Task RecordAsync(int organizationId, int workOrderId, string action, string actorType, int actorId)
    {
        try
        {
            _dbContext.AuditLogEntries.Add(new AuditLogEntry(organizationId, workOrderId, action, actorType, actorId, DateTime.UtcNow));
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record audit log entry for work order {WorkOrderId} ({Action})", workOrderId, action);
        }
    }
}
