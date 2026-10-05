using System.Text.Json;
using FieldOps.Api.Application;
using FieldOps.Api.Models;
using FieldOps.Modules.AuditLogs;
using FieldOps.Modules.Customers;
using Microsoft.AspNetCore.RateLimiting;
using FieldOps.Modules.Employees;
using FieldOps.Modules.WorkOrders;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Api.Controllers;

// Day 40: the tenant-isolation + membership pattern Week 8 discovered
// through real, live-found vulnerabilities (Days 35, 38, 39) is applied
// here from the start, not retrofitted after an exploit. Every action
// requires X-Organization-Id (which tenant) and X-Employee-Id (who, within
// that tenant), and verifies the acting employee's own OrganizationId
// matches the one being acted on — exactly EmployeesController's Day 39
// shape, this time correct from day one.
[ApiController]
[Route("api/[controller]")]
public class WorkOrdersController : ControllerBase
{
    private readonly IWorkOrderDirectory _workOrderDirectory;
    private readonly IEmployeeDirectory _employeeDirectory;
    private readonly ICustomerDirectory _customerDirectory;
    private readonly WorkOrderAssignmentService _workOrderAssignmentService;
    private readonly WorkOrderReportService _workOrderReportService;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IdempotencyService _idempotencyService;
    private readonly WorkOrderNoteSummaryService _workOrderNoteSummaryService;
    private readonly IWorkOrderSearchIndex _workOrderSearchIndex;
    private readonly ILogger<WorkOrdersController> _logger;

    public WorkOrdersController(
        IWorkOrderDirectory workOrderDirectory,
        IEmployeeDirectory employeeDirectory,
        ICustomerDirectory customerDirectory,
        WorkOrderAssignmentService workOrderAssignmentService,
        WorkOrderReportService workOrderReportService,
        IAuditLogWriter auditLogWriter,
        IdempotencyService idempotencyService,
        WorkOrderNoteSummaryService workOrderNoteSummaryService,
        IWorkOrderSearchIndex workOrderSearchIndex,
        ILogger<WorkOrdersController> logger)
    {
        _workOrderDirectory = workOrderDirectory;
        _employeeDirectory = employeeDirectory;
        _customerDirectory = customerDirectory;
        _workOrderAssignmentService = workOrderAssignmentService;
        _workOrderReportService = workOrderReportService;
        _auditLogWriter = auditLogWriter;
        _idempotencyService = idempotencyService;
        _workOrderNoteSummaryService = workOrderNoteSummaryService;
        _workOrderSearchIndex = workOrderSearchIndex;
        _logger = logger;
    }

    // Day 48 (Redis): the first genuinely expensive-to-repeat read in
    // FieldOps — now that WorkOrders is EF-backed (Day 48 earlier today),
    // this is a real SQL query every time, unlike everything before it,
    // which was reading from memory. Cache-aside via WorkOrderReportService.
    // Day 118: the three reads below (report, list, get-by-id) are async end
    // to end — Day 117's load test showed their blocking I/O starving the
    // thread pool. ASP.NET Core passes a CancellationToken that fires when
    // the client disconnects, so abandoned requests stop their queries.
    [HttpGet("report")]
    public async Task<ActionResult<WorkOrderStatusReport>> GetStatusReport(
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var report = await _workOrderReportService.GetStatusReportAsync(organizationId!.Value, cancellationToken);
        return Ok(report);
    }

    // Day 115: paged (defaults page 1, 50 per page, at most 100). The response
    // stays a plain array so existing clients keep working; no total count yet.
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkOrderDto>>> GetAll(
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        if (page < 1)
        {
            ModelState.AddModelError(nameof(page), "page must be 1 or greater.");
        }
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            ModelState.AddModelError(nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
        }
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var workOrders = (await _workOrderDirectory.GetPageByOrganizationAsync(organizationId!.Value, page, pageSize, cancellationToken))
            .Select(ToDto)
            .ToList();

        return Ok(workOrders);
    }

    // Day 115: the Angular detail page used to load the whole list and search
    // it client-side; once the list is paged that would miss work orders
    // beyond the first page. A work order of another organization returns
    // 404, not 403 — a 403 would confirm that the id exists (IDOR).
    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkOrderDto>> GetById(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var workOrder = await _workOrderDirectory.GetByIdAsync(id, cancellationToken);
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return NotFound();
        }

        return Ok(ToDto(workOrder));
    }

    // Day 79: goes through IWorkOrderSearchIndex (Elasticsearch), never
    // IWorkOrderDirectory (SQL Server) — this is deliberately the ONE
    // read in this controller that does not go to the source of truth,
    // because full-text relevance ranking is exactly what SQL's `LIKE`
    // does not give.
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<WorkOrderDto>>> Search(
        [FromQuery] string q,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var results = await _workOrderSearchIndex.SearchAsync(organizationId!.Value, q, cancellationToken);
        var workOrders = results
            .Select(d => new WorkOrderDto(d.Id, d.Title, d.OrganizationId, d.Status, null, Array.Empty<string>(), null, false))
            .ToList();

        return Ok(workOrders);
    }

    // Day 81: rebuild strategy — Admin-only (same precedent as Assign, Day
    // 41), since this is an operational action, not a normal business one.
    // Scoped to THIS organization only: it reads every one of its own work
    // orders straight from SQL Server (the source of truth) and rewrites
    // them into the search index directly, bypassing the outbox entirely —
    // there's no new business fact being announced here, just re-deriving a
    // copy that already-known, existing data.
    [HttpPost("search/rebuild")]
    public async Task<ActionResult> RebuildSearchIndex(
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var adminError = await ValidateIsAdminAsync(actingEmployeeId!.Value, "rebuild the search index for", cancellationToken);
        if (adminError is not null)
        {
            return adminError;
        }

        var documents = _workOrderDirectory.GetByOrganization(organizationId!.Value)
            .Select(w => new WorkOrderSearchDocument(w.Id, w.OrganizationId, w.Title, w.Status))
            .ToList();

        await _workOrderSearchIndex.RebuildOrganizationIndexAsync(organizationId!.Value, documents, cancellationToken);

        return NoContent();
    }

    // Day 53: per-organization rate limiting — only Create today, a
    // deliberately narrow scope. This is the one action that writes a brand
    // new row per call with no natural upper bound from an existing
    // resource (Assign/Complete/etc. all operate on one already-existing
    // work order, capping their own possible frequency).
    [HttpPost]
    [EnableRateLimiting("PerOrganization")]
    //
    // Day 119: Create, Assign, Start and Complete are async end to end — the
    // mixed load test showed their blocking I/O dragging read p95 from 31 ms
    // to 980 ms, since reads and writes share one thread pool.
    public async Task<ActionResult<WorkOrderDto>> Create(
        CreateWorkOrderRequest request,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        // Day 54: idempotency — an optional client-supplied key. If this
        // exact key was already used for a successful Create, replay that
        // same result instead of creating a second work order. A missing
        // key (the common case for most callers) skips this entirely,
        // exactly as before Day 54 — this is opt-in protection, not a
        // required contract change.
        if (idempotencyKey is not null)
        {
            var cachedResponse = await _idempotencyService.TryGetCachedResponseAsync(idempotencyKey);
            if (cachedResponse is not null)
            {
                return StatusCode(StatusCodes.Status201Created, cachedResponse);
            }
        }

        // Day 47: same generic "does not exist" hiding pattern as work
        // order/employee lookups elsewhere this week — a customer that
        // doesn't exist and one that belongs to another organization are
        // indistinguishable to the caller.
        if (request.CustomerId is not null)
        {
            var customer = await _customerDirectory.GetByIdAsync(request.CustomerId.Value, cancellationToken);
            if (customer is null || customer.OrganizationId != organizationId)
            {
                return BadRequest($"Customer {request.CustomerId} does not exist.");
            }
        }

        // Day 80: the search-index request is now built as an outbox entry,
        // not sent to Elasticsearch directly — OutboxPublisher delivers it
        // later, tolerating Elasticsearch being temporarily unreachable
        // (the exact problem Day 79 knowingly left open). The callback
        // shape exists because this WorkOrder's Id doesn't exist yet at
        // this point — IWorkOrderDirectory.Create calls it back with the
        // real, database-generated Id once it actually has one.
        var workOrder = await _workOrderDirectory.CreateAsync(request.Title, organizationId!.Value, request.CustomerId, newId =>
            [new OutboxEntry(
                nameof(WorkOrderSearchDocument),
                JsonSerializer.Serialize(new WorkOrderSearchDocument(newId, organizationId!.Value, request.Title, WorkOrderStatus.Open)))],
            cancellationToken);
        await _workOrderReportService.InvalidateCacheAsync(organizationId.Value);
        var dto = ToDto(workOrder);

        // Only the success path is remembered — a validation failure (the
        // two BadRequest returns above) is never cached, since the caller
        // may fix the request and legitimately needs to retry with real
        // effect, not get a replayed failure forever.
        if (idempotencyKey is not null)
        {
            await _idempotencyService.StoreResponseAsync(idempotencyKey, dto);
        }

        return StatusCode(StatusCodes.Status201Created, dto);
    }

    // Day 41: assignment is a state-changing action, not a read — unlike
    // GetAll's still-open "should a Member see the roster" question (Day 39),
    // "should any Member be able to assign any work order" isn't genuinely
    // ambiguous, so this reuses Create's Day 37 Admin-only precedent directly.
    [HttpPost("{id}/assign")]
    public async Task<ActionResult<WorkOrderDto>> Assign(
        int id,
        AssignWorkOrderRequest request,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var adminError = await ValidateIsAdminAsync(actingEmployeeId!.Value, "assign", cancellationToken);
        if (adminError is not null)
        {
            return adminError;
        }

        var result = await _workOrderAssignmentService.AssignWorkOrderAsync(id, request.EmployeeId, organizationId!.Value, cancellationToken);
        if (!result.Succeeded)
        {
            return BadRequest(result.Error);
        }

        await _workOrderReportService.InvalidateCacheAsync(organizationId!.Value);
        await _auditLogWriter.RecordAsync(organizationId!.Value, id, "Assigned", "Employee", actingEmployeeId!.Value);
        return Ok(ToDto(result.WorkOrder!));
    }

    // Day 44: reuses AssignWorkOrderRequest — identical shape (just an
    // EmployeeId), no reason for a separate ReassignWorkOrderRequest record.
    // Unlike Assign (Admin-only — deciding who gets brand-new work stays a
    // dispatcher decision), Reassign is FieldOps's first COMBINED
    // authorization rule: an Admin, OR the employee this work order is
    // currently assigned to, may hand it off — role (Day 37) OR ownership
    // (Day 42), not just one category at a time. The Assigned-or-InProgress
    // precondition (not Open, not Completed) still lives in the service.
    [HttpPost("{id}/reassign")]
    public ActionResult<WorkOrderDto> Reassign(
        int id,
        AssignWorkOrderRequest request,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var authError = ValidateIsAdminOrAssignee(id, organizationId, actingEmployeeId, "reassign");
        if (authError is not null)
        {
            return authError;
        }

        var result = _workOrderAssignmentService.ReassignWorkOrder(id, request.EmployeeId, organizationId!.Value);
        if (!result.Succeeded)
        {
            return BadRequest(result.Error);
        }

        return Ok(ToDto(result.WorkOrder!));
    }

    // Day 45: the simplest of the four mutations — no new employeeId to
    // validate, so no cross-module fact is needed and no
    // WorkOrderAssignmentService call is involved, unlike Assign/Reassign.
    // Reuses Day 44's ValidateIsAdminOrAssignee as-is (no new duplication):
    // an Admin, or the current assignee, may drop a work order back to
    // Open with nobody assigned.
    [HttpPost("{id}/unassign")]
    public ActionResult<WorkOrderDto> Unassign(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var authError = ValidateIsAdminOrAssignee(id, organizationId, actingEmployeeId, "unassign");
        if (authError is not null)
        {
            return authError;
        }

        var updated = _workOrderDirectory.Unassign(id);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be Assigned or InProgress before it can be unassigned.");
        }

        _workOrderReportService.InvalidateCache(organizationId!.Value);
        return Ok(ToDto(updated));
    }

    // Day 45 independent-task addition: without this, Completed was a
    // permanent dead end. Deliberately Admin-only (reuses ValidateIsAdmin's
    // role check, Assign's precedent) rather than Reassign/Unassign's
    // combined rule — un-completing work is a higher-stakes correction (the
    // assignee already declared it done), not something the assignee
    // should be able to reverse unilaterally at will.
    //
    // A real, live-caught bug during this exact write-up: the first version
    // called ValidateIsAdmin alone, which only checks the ACTING employee's
    // role — never whether the target work order belongs to their
    // organization at all. Live-proven exploit: an Org 1 Admin could reopen
    // Org 2's completed work order just by naming its id. Assign avoids
    // this because WorkOrderAssignmentService's ValidateWorkOrderAndEmployee
    // checks the work order's organization downstream; Reopen calls
    // IWorkOrderDirectory directly with no such layer, so the check has to
    // happen here instead.
    [HttpPost("{id}/reopen")]
    public ActionResult<WorkOrderDto> Reopen(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var adminError = ValidateIsAdminForWorkOrder(id, organizationId, actingEmployeeId, "reopen");
        if (adminError is not null)
        {
            return adminError;
        }

        var updated = _workOrderDirectory.Reopen(id);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be Completed before it can be reopened.");
        }

        _workOrderReportService.InvalidateCache(organizationId!.Value);
        return Ok(ToDto(updated));
    }

    // Day 42: ownership-based authorization — a third kind alongside Day 35's
    // tenant membership and Day 37's role. "Is the caller the specific
    // employee this work order was assigned to" isn't a group fact (any org
    // member, any Admin); it's a fact about this ONE record, so it's checked
    // here in the controller, not inside the module, using AssignedEmployeeId
    // — a field the module already exposes, no cross-module lookup needed.
    [HttpPost("{id}/start")]
    public async Task<ActionResult<WorkOrderDto>> Start(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var (ownershipError, _) = await ValidateOwnershipAsync(id, organizationId, actingEmployeeId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }

        var updated = await _workOrderDirectory.StartAsync(id, cancellationToken);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be Assigned before it can be started.");
        }

        await _workOrderReportService.InvalidateCacheAsync(organizationId!.Value);
        return Ok(ToDto(updated));
    }

    // Day 51: originally the only async action in this controller (awaiting
    // a notification send). Day 71's Outbox pattern moved event publishing
    // out of the request entirely, making this synchronous. Day 79
    // reintroduced an await for search indexing; Day 80 moves that onto the
    // SAME outbox mechanism, so this is synchronous again — indexing no
    // longer happens inside this request at all.
    [HttpPost("{id}/complete")]
    public async Task<ActionResult<WorkOrderDto>> Complete(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = await ValidateMembershipAsync(organizationId, actingEmployeeId, cancellationToken);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var (ownershipError, workOrderBeforeCompletion) = await ValidateOwnershipAsync(id, organizationId, actingEmployeeId, cancellationToken);
        if (ownershipError is not null)
        {
            return ownershipError;
        }

        // Day 71: built BEFORE Complete() runs, from the work order's
        // already-known Title/CustomerId (neither changes during Complete) —
        // this is the exact payload that will end up in one of the outbox
        // rows, written atomically alongside the Status change itself.
        var eventPayload = JsonSerializer.Serialize(
            new WorkOrderCompletedEvent(id, organizationId!.Value, workOrderBeforeCompletion!.CustomerId, workOrderBeforeCompletion.Title, DateTime.UtcNow));

        // Day 80: a SECOND outbox entry, alongside the first — Complete's Id
        // is already known (unlike Create's), so no callback is needed here,
        // just a plain list built up front.
        var indexPayload = JsonSerializer.Serialize(
            new WorkOrderSearchDocument(id, organizationId!.Value, workOrderBeforeCompletion.Title, WorkOrderStatus.Completed));

        var updated = await _workOrderDirectory.CompleteAsync(id, [
            new OutboxEntry(nameof(WorkOrderCompletedEvent), eventPayload),
            new OutboxEntry(nameof(WorkOrderSearchDocument), indexPayload)
        ], cancellationToken);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be InProgress before it can be completed.");
        }

        await _workOrderReportService.InvalidateCacheAsync(organizationId!.Value);
        await _auditLogWriter.RecordAsync(organizationId!.Value, id, "Completed", "Employee", actingEmployeeId!.Value);

        // Day 86: incremented only on the genuine success path — the same
        // "only count it once the real thing happened" discipline as every
        // other side effect in this action.
        FieldOpsMetrics.WorkOrdersCompleted.Add(1);

        return Ok(ToDto(updated));
    }

    // Day 46: reuses ValidateOwnership (Day 42) unchanged — evidence is
    // attached by the person doing the work, the same actor as Start/Complete,
    // not the combined Admin-or-assignee rule Reassign/Unassign use. A real
    // file/photo isn't stored (no upload infrastructure yet) — Note is a
    // deliberate demo stand-in.
    [HttpPost("{id}/evidence")]
    public ActionResult<WorkOrderDto> AddEvidence(
        int id,
        AddEvidenceRequest request,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var ownershipError = ValidateOwnership(id, organizationId, actingEmployeeId, out _);
        if (ownershipError is not null)
        {
            return ownershipError;
        }

        var updated = _workOrderDirectory.AddEvidence(id, request.Note);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be assigned before evidence can be added.");
        }

        return Ok(ToDto(updated));
    }

    // Day 63: the first read that goes through the new IAiProvider seam.
    // Reuses ValidateMembership only — not ValidateOwnership — since reading
    // a summary of already-recorded evidence is a plain organization-wide
    // read, the same access level as GetAll/GetStatusReport, not an
    // ownership-restricted action like Start/Complete/AddEvidence.
    //
    // Day 64: unlike Complete's notification (Day 51), where a failure is a
    // side effect that must never block an already-successful state change,
    // the summary IS this action's entire purpose — a failure here can't be
    // silently swallowed, but it also shouldn't surface as a raw, unhelpful
    // 500. A real AI provider can fail (timeout, network error, rate limit)
    // in ways FakeAiProvider never does, so this boundary is exercised
    // deliberately, not left untested until a real provider exists.
    [HttpGet("{id}/summary")]
    public async Task<ActionResult<string>> GetSummary(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Employee-Id")] int? actingEmployeeId,
        CancellationToken cancellationToken)
    {
        var membershipError = ValidateMembership(organizationId, actingEmployeeId);
        if (membershipError is not null)
        {
            return membershipError;
        }

        var workOrder = _workOrderDirectory.GetById(id);
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return BadRequest($"Work order {id} does not exist.");
        }

        try
        {
            var summary = await _workOrderNoteSummaryService.SummarizeAsync(workOrder.EvidenceNotes, cancellationToken);
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI summary for work order {WorkOrderId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "AI summary service is temporarily unavailable. Please try again later.");
        }
    }

    // Day 47: a FOURTH kind of authorization actor — not an Employee at
    // all, unlike every check so far (tenant membership, role, ownership).
    // X-Customer-Id is the same deliberate simplification class as
    // X-Employee-Id (Day 35): a plain, unverified, client-stated header —
    // no real customer portal/authentication exists yet. "Is this customer
    // actually the one linked to this work order" mirrors Day 42's
    // ownership shape, just for a different identity type entirely.
    [HttpPost("{id}/approve")]
    public ActionResult<WorkOrderDto> Approve(
        int id,
        [FromHeader(Name = "X-Organization-Id")] int? organizationId,
        [FromHeader(Name = "X-Customer-Id")] int? actingCustomerId)
    {
        if (organizationId is null)
        {
            return BadRequest("X-Organization-Id header is required.");
        }

        if (actingCustomerId is null)
        {
            return BadRequest("X-Customer-Id header is required.");
        }

        var actingCustomer = _customerDirectory.GetById(actingCustomerId.Value);
        if (actingCustomer is null || actingCustomer.OrganizationId != organizationId)
        {
            return BadRequest($"Customer {actingCustomerId} does not exist.");
        }

        var workOrder = _workOrderDirectory.GetById(id);
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return BadRequest($"Work order {id} does not exist.");
        }

        if (workOrder.CustomerId != actingCustomerId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only this work order's own customer can approve it.");
        }

        var updated = _workOrderDirectory.Approve(id);
        if (updated is null)
        {
            return BadRequest($"Work order {id} must be Completed before it can be approved.");
        }

        _auditLogWriter.Record(organizationId.Value, id, "Approved", "Customer", actingCustomerId.Value);
        return Ok(ToDto(updated));
    }

    // Day 43: extracted for Assign specifically. Day 44 moved Reassign onto
    // ValidateIsAdminOrAssignee below (a different, combined rule), so this
    // one now has a single caller again — kept as its own named method
    // anyway since "Assign is Admin-only" is a real, standalone business
    // rule worth naming, not just inlined into Assign's action body.
    private async Task<ActionResult?> ValidateIsAdminAsync(int actingEmployeeId, string action, CancellationToken cancellationToken)
    {
        // ValidateMembership already looked this employee up once — looked
        // up again here since only Assign needs the role, and adding an
        // out-parameter to ValidateMembership purely for this one caller
        // would complicate a helper the other actions don't need changed.
        // A real, negligible cost against an in-memory list; worth
        // revisiting once a real database makes lookups non-free.
        // Day 119: async (Assign, its only caller, is async now). The second
        // lookup is now a real database round trip — noted, not changed today.
        var actingEmployee = (await _employeeDirectory.GetByIdAsync(actingEmployeeId, cancellationToken))!;
        if (actingEmployee.Role != EmployeeRole.Admin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"Only an Admin can {action} work orders.");
        }

        return null;
    }

    // Day 44: FieldOps's first combined authorization check — role OR
    // ownership, not just one category. Fetches the work order itself
    // (unlike ValidateIsAdmin, which only needs the acting employee) since
    // "are you the assignee" requires knowing who the assignee IS. Reuses
    // Day 41's generic "does not exist" message for a missing/cross-org
    // work order — the same information-hiding principle applied here too.
    private ActionResult? ValidateIsAdminOrAssignee(int workOrderId, int? organizationId, int? actingEmployeeId, string action)
    {
        var actingEmployee = _employeeDirectory.GetById(actingEmployeeId!.Value)!;

        var workOrder = _workOrderDirectory.GetById(workOrderId);
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return BadRequest($"Work order {workOrderId} does not exist.");
        }

        if (actingEmployee.Role != EmployeeRole.Admin && workOrder.AssignedEmployeeId != actingEmployeeId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"Only an Admin or the assigned employee can {action} this work order.");
        }

        return null;
    }

    // Day 45 bug fix: Reopen's Admin-only rule, but — unlike ValidateIsAdmin
    // — also confirms the work order itself belongs to the caller's
    // organization. Deliberately kept separate from ValidateIsAdminOrAssignee
    // above rather than merged: the two aren't identical (this one has no
    // ownership OR-branch at all), so forcing them into one method would be
    // exactly the "not really the same" trap Day 39/40 warned about.
    private ActionResult? ValidateIsAdminForWorkOrder(int workOrderId, int? organizationId, int? actingEmployeeId, string action)
    {
        var actingEmployee = _employeeDirectory.GetById(actingEmployeeId!.Value)!;

        var workOrder = _workOrderDirectory.GetById(workOrderId);
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return BadRequest($"Work order {workOrderId} does not exist.");
        }

        if (actingEmployee.Role != EmployeeRole.Admin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, $"Only an Admin can {action} work orders.");
        }

        return null;
    }

    // Shared by Start/Complete — same "does this work order genuinely exist,
    // for me" generic-message pattern as Day 41's Assign (a work order that
    // doesn't exist and one belonging to another organization are
    // indistinguishable), plus the new ownership check.
    //
    // Day 119: an async method can't have an out parameter, so the async
    // version returns (error, workOrder) as a tuple; both versions share the
    // rule in CheckOwnership. The sync one remains for AddEvidence.
    private ActionResult? ValidateOwnership(int workOrderId, int? organizationId, int? actingEmployeeId, out WorkOrderSummary? workOrder)
    {
        workOrder = _workOrderDirectory.GetById(workOrderId);
        var error = CheckOwnership(workOrderId, organizationId, actingEmployeeId, workOrder);
        if (error is not null)
        {
            workOrder = null;
        }
        return error;
    }

    private async Task<(ActionResult? Error, WorkOrderSummary? WorkOrder)> ValidateOwnershipAsync(int workOrderId, int? organizationId, int? actingEmployeeId, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderDirectory.GetByIdAsync(workOrderId, cancellationToken);
        var error = CheckOwnership(workOrderId, organizationId, actingEmployeeId, workOrder);
        return (error, error is null ? workOrder : null);
    }

    private ActionResult? CheckOwnership(int workOrderId, int? organizationId, int? actingEmployeeId, WorkOrderSummary? workOrder)
    {
        if (workOrder is null || workOrder.OrganizationId != organizationId)
        {
            return BadRequest($"Work order {workOrderId} does not exist.");
        }

        if (workOrder.AssignedEmployeeId != actingEmployeeId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only the assigned employee can act on this work order.");
        }

        return null;
    }

    private static WorkOrderDto ToDto(WorkOrderSummary workOrder) =>
        new(workOrder.Id, workOrder.Title, workOrder.OrganizationId, workOrder.Status, workOrder.AssignedEmployeeId, workOrder.EvidenceNotes, workOrder.CustomerId, workOrder.CustomerApproved);

    // Shared by both actions today — unlike EmployeesController (Day 39),
    // where the identical duplication between Create/GetAll was deliberately
    // left alone (only two call sites, "rule of three" not yet met), this
    // controller starts with two call sites needing the EXACT same check
    // from day one, with no additional per-action check layered on top
    // (EmployeesController.Create's extra role check is what made its two
    // call sites non-identical). Extracting here avoids writing the same
    // three checks twice within a single new file.
    //
    // Day 118: two entry points — the synchronous one for the write actions
    // (still synchronous for now) and an async one for the async reads. They
    // differ only in how the acting employee is fetched; the rules themselves
    // live once, in MissingHeaders and CheckMembership.
    private ActionResult? ValidateMembership(int? organizationId, int? actingEmployeeId) =>
        MissingHeaders(organizationId, actingEmployeeId)
        ?? CheckMembership(organizationId!.Value, actingEmployeeId!.Value, _employeeDirectory.GetById(actingEmployeeId.Value));

    private async Task<ActionResult?> ValidateMembershipAsync(int? organizationId, int? actingEmployeeId, CancellationToken cancellationToken) =>
        MissingHeaders(organizationId, actingEmployeeId)
        ?? CheckMembership(organizationId!.Value, actingEmployeeId!.Value, await _employeeDirectory.GetByIdAsync(actingEmployeeId.Value, cancellationToken));

    private ActionResult? MissingHeaders(int? organizationId, int? actingEmployeeId)
    {
        if (organizationId is null)
        {
            return BadRequest("X-Organization-Id header is required.");
        }

        if (actingEmployeeId is null)
        {
            return BadRequest("X-Employee-Id header is required.");
        }

        return null;
    }

    private ActionResult? CheckMembership(int organizationId, int actingEmployeeId, EmployeeSummary? actingEmployee)
    {
        if (actingEmployee is null)
        {
            return BadRequest($"Employee {actingEmployeeId} does not exist.");
        }

        if (actingEmployee.OrganizationId != organizationId)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "You can only act within your own organization.");
        }

        return null;
    }
}
