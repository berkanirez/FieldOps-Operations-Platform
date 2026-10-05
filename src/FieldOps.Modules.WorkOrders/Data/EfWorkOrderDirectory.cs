using FieldOps.Modules.WorkOrders.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Modules.WorkOrders.Data;

// internal, replacing InMemoryWorkOrderDirectory (Day 40) as
// IWorkOrderDirectory's real implementation — every state-machine rule
// (Day 41-46) is unchanged, just persisted via SaveChanges() instead of
// living only in a List<WorkOrder>. Still deliberately synchronous.
internal class EfWorkOrderDirectory : IWorkOrderDirectory
{
    private readonly WorkOrdersDbContext _dbContext;

    public EfWorkOrderDirectory(WorkOrdersDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // Day 113: Where runs BEFORE ToList, on IQueryable, so EF Core turns it
    // into "WHERE [w].[OrganizationId] = @organizationId" — only this
    // organization's rows ever leave the database.
    public IReadOnlyList<WorkOrderSummary> GetByOrganization(int organizationId)
    {
        return _dbContext.WorkOrders
            .Where(w => w.OrganizationId == organizationId)
            .Select(ToSummary)
            .ToList();
    }

    // Day 115: OrderBy + Skip + Take on IQueryable become
    // "ORDER BY [w].[Id] OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY".
    // Day 118: ToListAsync — same SQL, but the thread goes back to the pool
    // while the query runs; cancellationToken aborts it if the caller leaves.
    public async Task<IReadOnlyList<WorkOrderSummary>> GetPageByOrganizationAsync(int organizationId, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        // ToSummary is a method group, so .Select(ToSummary) would bind to the
        // in-memory Enumerable.Select (no async there): fetch the page
        // asynchronously first, then map in memory — the SQL is unchanged.
        var page = await _dbContext.WorkOrders
            .Where(w => w.OrganizationId == organizationId)
            .OrderBy(w => w.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return page.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyDictionary<WorkOrderStatus, int>> GetStatusCountsAsync(int organizationId, CancellationToken cancellationToken)
    {
        return await _dbContext.WorkOrders
            .Where(w => w.OrganizationId == organizationId)
            .GroupBy(w => w.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
    }

    public WorkOrderSummary? GetById(int id)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == id);
        return workOrder is null ? null : ToSummary(workOrder);
    }

    public async Task<WorkOrderSummary?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var workOrder = await _dbContext.WorkOrders.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        return workOrder is null ? null : ToSummary(workOrder);
    }

    public async Task<WorkOrderSummary> CreateAsync(string title, int organizationId, int? customerId, Func<int, IReadOnlyList<OutboxEntry>> buildOutboxEntries, CancellationToken cancellationToken)
    {
        // Day 80: an explicit transaction because this Create, unlike
        // Complete, genuinely needs TWO SaveChanges calls — the WorkOrder's
        // Id doesn't exist until the first one runs, but the outbox entries
        // built from it must land in the SAME atomic unit as the WorkOrder
        // itself. Without this transaction, a crash between the two calls
        // could leave a WorkOrder that exists but will never be indexed.
        //
        // Day 111: with EnableRetryOnFailure, EF Core refuses a user-started
        // transaction outright (proven live: InvalidOperationException) —
        // retrying one SaveChanges in the middle of it could half-apply the
        // unit. The execution strategy instead retries the WHOLE block:
        // transaction, both saves, commit. Clearing the change tracker first
        // matters on a retry: the previous attempt's WorkOrder and outbox
        // rows are still tracked as Added, and would otherwise be inserted
        // alongside the new attempt's — duplicates.
        //
        // Day 119: async — ExecuteAsync retries the whole async block exactly
        // as Execute did; every step inside is awaited.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var workOrder = new WorkOrder(title, organizationId, WorkOrderStatus.Open) { CustomerId = customerId };
            _dbContext.WorkOrders.Add(workOrder);
            await _dbContext.SaveChangesAsync(cancellationToken);
            // workOrder.Id is now populated by the database's IDENTITY column.

            foreach (var entry in buildOutboxEntries(workOrder.Id))
            {
                _dbContext.OutboxMessages.Add(new OutboxMessage(entry.EventType, entry.Payload));
            }
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return ToSummary(workOrder);
        });
    }

    public async Task<WorkOrderSummary?> AssignAsync(int workOrderId, int employeeId, CancellationToken cancellationToken)
    {
        var workOrder = await _dbContext.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId, cancellationToken);
        if (workOrder is null || workOrder.Status != WorkOrderStatus.Open)
        {
            return null;
        }

        workOrder.Status = WorkOrderStatus.Assigned;
        workOrder.AssignedEmployeeId = employeeId;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(workOrder);
    }

    public async Task<WorkOrderSummary?> StartAsync(int workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await _dbContext.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId, cancellationToken);
        if (workOrder is null || workOrder.Status != WorkOrderStatus.Assigned)
        {
            return null;
        }

        workOrder.Status = WorkOrderStatus.InProgress;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(workOrder);
    }

    public async Task<WorkOrderSummary?> CompleteAsync(int workOrderId, IReadOnlyList<OutboxEntry> outboxEntries, CancellationToken cancellationToken)
    {
        var workOrder = await _dbContext.WorkOrders.FirstOrDefaultAsync(w => w.Id == workOrderId, cancellationToken);
        if (workOrder is null || workOrder.Status != WorkOrderStatus.InProgress)
        {
            return null;
        }

        workOrder.Status = WorkOrderStatus.Completed;

        // Day 71: the Outbox pattern's whole point — every Add below and
        // the Status change above are tracked by the SAME DbContext and
        // committed by the SAME SaveChanges call, so either all of them
        // land or none do. There is no window where the work order is
        // Completed in the database but one of its outbox rows is missing.
        // Day 80: now possibly MORE than one row per call (e.g. a
        // WorkOrderCompletedEvent row and a search-index-request row).
        foreach (var entry in outboxEntries)
        {
            _dbContext.OutboxMessages.Add(new OutboxMessage(entry.EventType, entry.Payload));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToSummary(workOrder);
    }

    public WorkOrderSummary? Reassign(int workOrderId, int newEmployeeId)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null || (workOrder.Status != WorkOrderStatus.Assigned && workOrder.Status != WorkOrderStatus.InProgress))
        {
            return null;
        }

        workOrder.AssignedEmployeeId = newEmployeeId;
        _dbContext.SaveChanges();
        return ToSummary(workOrder);
    }

    public WorkOrderSummary? Unassign(int workOrderId)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null || (workOrder.Status != WorkOrderStatus.Assigned && workOrder.Status != WorkOrderStatus.InProgress))
        {
            return null;
        }

        workOrder.Status = WorkOrderStatus.Open;
        workOrder.AssignedEmployeeId = null;
        _dbContext.SaveChanges();
        return ToSummary(workOrder);
    }

    public WorkOrderSummary? Reopen(int workOrderId)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null || workOrder.Status != WorkOrderStatus.Completed)
        {
            return null;
        }

        workOrder.Status = WorkOrderStatus.InProgress;
        _dbContext.SaveChanges();
        return ToSummary(workOrder);
    }

    public WorkOrderSummary? AddEvidence(int workOrderId, string note)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null || workOrder.Status == WorkOrderStatus.Open)
        {
            return null;
        }

        workOrder.EvidenceNotes.Add(note);
        _dbContext.SaveChanges();
        return ToSummary(workOrder);
    }

    public WorkOrderSummary? Approve(int workOrderId)
    {
        var workOrder = _dbContext.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is null || workOrder.Status != WorkOrderStatus.Completed)
        {
            return null;
        }

        workOrder.CustomerApproved = true;
        _dbContext.SaveChanges();
        return ToSummary(workOrder);
    }

    public IReadOnlyList<OutboxMessageSummary> GetUnpublishedOutboxMessages()
    {
        return _dbContext.OutboxMessages
            .Where(m => m.PublishedAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new OutboxMessageSummary(m.Id, m.EventType, m.Payload, m.CreatedAtUtc))
            .ToList();
    }

    public void MarkOutboxMessagePublished(int outboxMessageId)
    {
        var message = _dbContext.OutboxMessages.FirstOrDefault(m => m.Id == outboxMessageId);
        if (message is not null)
        {
            message.PublishedAtUtc = DateTime.UtcNow;
            _dbContext.SaveChanges();
        }
    }

    public bool HasProcessedMessage(string consumerName, string messageId)
    {
        return _dbContext.ProcessedMessages.Any(m => m.ConsumerName == consumerName && m.MessageId == messageId);
    }

    public void MarkMessageProcessed(string consumerName, string messageId)
    {
        try
        {
            _dbContext.ProcessedMessages.Add(new ProcessedMessage(consumerName, messageId));
            _dbContext.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Day 19's exact two-layer pattern: HasProcessedMessage above
            // already covers the normal case; this catch is only a safety
            // net for the rare race where the same message is processed
            // concurrently — the database's own unique index (not this
            // code) is what actually decides which insert wins.
        }
    }

    public int RecordFailedAttempt(string consumerName, string messageId)
    {
        var attempt = _dbContext.FailedMessageAttempts
            .FirstOrDefault(m => m.ConsumerName == consumerName && m.MessageId == messageId);

        if (attempt is null)
        {
            attempt = new FailedMessageAttempt(consumerName, messageId);
            _dbContext.FailedMessageAttempts.Add(attempt);
        }

        attempt.AttemptCount++;
        _dbContext.SaveChanges();
        return attempt.AttemptCount;
    }

    private static WorkOrderSummary ToSummary(WorkOrder workOrder) =>
        new(workOrder.Id, workOrder.Title, workOrder.OrganizationId, workOrder.Status, workOrder.AssignedEmployeeId, workOrder.EvidenceNotes.AsReadOnly(), workOrder.CustomerId, workOrder.CustomerApproved);
}
