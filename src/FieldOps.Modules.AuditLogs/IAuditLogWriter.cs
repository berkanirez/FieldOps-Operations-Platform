namespace FieldOps.Modules.AuditLogs;

// Day 52: an append-only "who/what/when" ledger. Write-only today — no
// GetAll/GetById, unlike every other module's directory interface, because
// there is no read/reporting feature for this data yet (deliberately
// deferred, same as ICustomerDirectory's still-missing Create on Day 47).
public interface IAuditLogWriter
{
    void Record(int organizationId, int workOrderId, string action, string actorType, int actorId);

    // Day 119: async for Assign/Complete. Deliberately no CancellationToken:
    // the audit entry describes a change that has already been saved, so a
    // client disconnecting at that moment must not cancel its audit record.
    Task RecordAsync(int organizationId, int workOrderId, string action, string actorType, int actorId);
}
