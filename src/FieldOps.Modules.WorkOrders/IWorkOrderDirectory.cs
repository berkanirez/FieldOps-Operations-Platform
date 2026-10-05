namespace FieldOps.Modules.WorkOrders;

// Deliberately does NOT validate that organizationId refers to a real
// Organization — same reasoning as IEmployeeDirectory (Day 33, ADR 0002):
// this module has no reference to FieldOps.Modules.Organizations at all.
// That validation, and the caller's membership within that organization,
// is the host's job (see FieldOps.Api's WorkOrdersController).
public interface IWorkOrderDirectory
{
    // Day 113: replaces GetAll(), which returned EVERY organization's work
    // orders for callers to filter in memory — measured with 50,000 rows of
    // another organization, every list/report request read all of them
    // (SQL had no WHERE). Both queries below filter in SQL; there is
    // deliberately no "get everything" method left to misuse.
    IReadOnlyList<WorkOrderSummary> GetByOrganization(int organizationId);

    // Day 115: the list endpoint's query — ordered by Id (paging without a
    // fixed order can repeat or skip rows) and bounded, so the seek + lookup
    // plan Day 114 measured stays cheap for any organization size.
    // GetByOrganization above stays unbounded for the Admin-only search index
    // rebuild, which genuinely needs every work order of the organization.
    //
    // Day 118: async — these three are the read path Day 117's load test
    // exercises, where blocking I/O starved the thread pool.
    Task<IReadOnlyList<WorkOrderSummary>> GetPageByOrganizationAsync(int organizationId, int page, int pageSize, CancellationToken cancellationToken);

    // Counted by the database (GROUP BY Status) — only one row per status
    // comes back, never the work orders themselves. A status with no work
    // orders is simply absent from the dictionary.
    Task<IReadOnlyDictionary<WorkOrderStatus, int>> GetStatusCountsAsync(int organizationId, CancellationToken cancellationToken);

    // The write actions (Assign, Start, Complete, ...) still use the
    // synchronous GetById below — converting the write path is a later step.
    WorkOrderSummary? GetById(int id);
    Task<WorkOrderSummary?> GetByIdAsync(int id, CancellationToken cancellationToken);

    // Day 47: customerId is optional and, like organizationId, not
    // validated here — this module has no reference to
    // FieldOps.Modules.Customers at all (ADR 0002). The host
    // (WorkOrdersController) checks the customer exists and belongs to the
    // right organization before ever calling this.
    //
    // Day 80: buildOutboxEntries is a CALLBACK, not a plain list, for a
    // concrete reason — the new WorkOrder's Id doesn't exist yet when this
    // method is called (it's database-generated), but the host's outbox
    // payload (e.g. a search-index request) needs that real Id inside it.
    // This module calls the callback with the Id only once it's genuinely
    // known, then persists whatever opaque entries come back — in the SAME
    // transaction as the WorkOrder's own insert, so a crash between the two
    // writes leaves neither behind, never just one.
    //
    // Day 119: Create, Assign, Start and Complete are async — the write flow
    // the mixed load test exercises. Their synchronous versions are gone; the
    // remaining write methods below are converted later.
    Task<WorkOrderSummary> CreateAsync(string title, int organizationId, int? customerId, Func<int, IReadOnlyList<OutboxEntry>> buildOutboxEntries, CancellationToken cancellationToken);

    // Deliberately does NOT validate that employeeId refers to a real
    // Employee, or that it belongs to the same organization as this work
    // order — same ADR 0002 reasoning as Create's organizationId. Those are
    // cross-module facts only the host can check (WorkOrderAssignmentService).
    // The "must currently be Open" rule is different: it's a fact purely
    // about a WorkOrder's own state, so this module enforces it itself
    // rather than trusting the host to remember. Returns null if no work
    // order with this id exists, or if it isn't currently Open.
    Task<WorkOrderSummary?> AssignAsync(int workOrderId, int employeeId, CancellationToken cancellationToken);

    // Day 42: neither of these takes an employeeId. Unlike Assign (a
    // cross-module fact — is this employee real, is it in the right
    // organization), "is the caller the one this work order was assigned
    // to" only needs a WorkOrder's own AssignedEmployeeId field — the host
    // (WorkOrdersController) checks that itself before calling here, the
    // same place Day 37's role checks live. This module only enforces its
    // own state-machine invariant: Start requires Assigned, Complete
    // requires InProgress. Returns null if the work order doesn't exist or
    // isn't in the required prior state.
    Task<WorkOrderSummary?> StartAsync(int workOrderId, CancellationToken cancellationToken);

    // Day 71: outboxEntries are opaque to this module — it never interprets
    // them, just persists them in the SAME SaveChanges call as the Status
    // change (the Outbox pattern's actual guarantee). The host builds each
    // entry's EventType/Payload (typically nameof(SomeEvent) and
    // JsonSerializer.Serialize(someEvent)) since only the host knows what a
    // "WorkOrderCompletedEvent" even is (ADR 0002 — no FieldOps.Api type
    // reference exists in this module).
    //
    // Day 80: a plain list here (not Create's callback above) — Complete
    // acts on a WorkOrder that already exists, with an already-known Id, so
    // there's no generated value the host needs to wait for.
    Task<WorkOrderSummary?> CompleteAsync(int workOrderId, IReadOnlyList<OutboxEntry> outboxEntries, CancellationToken cancellationToken);

    // Day 43: changes WHO is assigned without changing Status — unlike
    // Assign (Open -> Assigned), Reassign only makes sense while a work
    // order is already Assigned or InProgress (someone was doing it, now
    // someone else will). Returns null if the work order doesn't exist or
    // isn't currently in one of those two states.
    WorkOrderSummary? Reassign(int workOrderId, int newEmployeeId);

    // Day 45: clears the assignment entirely and returns to Open — the
    // simplest of the four mutations, since (unlike Assign/Reassign) there's
    // no new employeeId to validate at all, no cross-module fact needed.
    // Same prior-state requirement as Reassign (Assigned or InProgress);
    // unassigning an already-Open or Completed work order makes no sense.
    WorkOrderSummary? Unassign(int workOrderId);

    // Day 45 independent-task addition: without this, Completed was a
    // permanent dead end — no transition anywhere accepted it as a starting
    // state. Reopens back to InProgress (not Open/Assigned) since the
    // original assignee is still on record and simply resumes; returns
    // null if the work order doesn't exist or isn't currently Completed.
    WorkOrderSummary? Reopen(int workOrderId);

    // Day 46: "must not be Open" is the only state rule — nobody's doing
    // anything yet if nobody's assigned, so there's nothing to attach
    // evidence to. Every other status (Assigned/InProgress/Completed) is
    // valid, unlike the other mutations which each need one exact prior
    // state. Returns null if the work order doesn't exist or is still Open.
    WorkOrderSummary? AddEvidence(int workOrderId, string note);

    // Day 47: only enforces the state-machine invariant (must be Completed)
    // — same split as Start/Complete/Reassign/Unassign: "is the caller
    // actually the linked customer" is a host-level check (the host has
    // ICustomerDirectory, this module never will). Returns null if the
    // work order doesn't exist or isn't Completed.
    WorkOrderSummary? Approve(int workOrderId);

    // Day 71: read side of the Outbox pattern — OutboxPublisher (a host
    // BackgroundService) polls this to find rows Complete() wrote but
    // nothing has published to RabbitMQ yet.
    IReadOnlyList<OutboxMessageSummary> GetUnpublishedOutboxMessages();

    // Marks one row published so it's never picked up again. A separate
    // call (not part of Complete) since publishing happens later, in a
    // different scope/transaction, once IEventPublisher actually succeeds.
    void MarkOutboxMessagePublished(int outboxMessageId);

    // Day 73: the Inbox pattern — consumerName/messageId are as opaque to
    // this module as outboxEventType/outboxPayload already are; it's just
    // asked "has THIS consumer already handled THIS message ID" and told
    // "record that it now has," never what either string actually means.
    bool HasProcessedMessage(string consumerName, string messageId);
    void MarkMessageProcessed(string consumerName, string messageId);

    // Day 74: dead-letter queue support — records one more failed attempt
    // for this (consumer, message) pair and returns the new total count, so
    // the host can decide "retry again" versus "give up, dead-letter it"
    // without needing to know how or where that count is stored.
    int RecordFailedAttempt(string consumerName, string messageId);
}
