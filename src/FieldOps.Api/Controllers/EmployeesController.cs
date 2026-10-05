using FieldOps.Api.Application;
using FieldOps.Api.Models;
using FieldOps.Modules.Employees;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EmployeesController : ControllerBase
{
    private readonly IEmployeeDirectory _employeeDirectory;
    private readonly EmployeeApplicationService _employeeApplicationService;

    public EmployeesController(IEmployeeDirectory employeeDirectory, EmployeeApplicationService employeeApplicationService)
    {
        _employeeDirectory = employeeDirectory;
        _employeeApplicationService = employeeApplicationService;
    }

    // Day 35: [FromQuery] int? organizationId = null used to make tenant
    // scoping OPTIONAL and entirely client-controlled — proven live to leak
    // every organization's employees together when omitted, and to let any
    // caller request any other organization's data just by passing its id.
    //
    // First fix attempt used a non-nullable [FromHeader] int, on the
    // (untested) assumption that a missing header would fail model binding
    // with a 400. Live-checked and found FALSE: a missing header silently
    // bound to 0 instead, returning 200 with an empty list — not a rejection
    // at all, just a query that happened to match nothing. Using `int?`
    // instead lets a genuinely absent header be told apart from any real
    // value, so it can be rejected explicitly.
    //
    // Day 39: even after that fix, this action still had NO identity check
    // at all — X-Organization-Id alone was enough, X-Employee-Id was never
    // asked for. Live-proven exploit: anyone, with zero credentials, could
    // read any organization's full employee list just by naming its id.
    // Brought up to the same membership standard as Create (Day 38): a real,
    // existing employee whose own OrganizationId matches the one being
    // queried. Deliberately NOT role-restricted (Admin vs Member) — that
    // stays the open product question from Day 37's independent task; this
    // only closes the "no identity at all" hole.
    //
    // Day 123: identity from the validated token (SECURITY_REVIEW.md F1), not
    // headers; the "list another organization by naming its id" exploit is
    // structurally gone — the organization IS the caller's. The membership
    // check stays for the same reason as in WorkOrdersController (a token
    // can outlive the employee or their organization). The organization
    // filter now runs in SQL (Day 113's lesson).
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmployeeDto>>> GetAll(CancellationToken cancellationToken)
    {
        var (actingEmployee, identityError) = await GetActingEmployeeAsync(cancellationToken);
        if (identityError is not null)
        {
            return identityError;
        }

        var employees = (await _employeeDirectory.GetByOrganizationAsync(actingEmployee!.OrganizationId, cancellationToken))
            .Select(e => new EmployeeDto(e.Id, e.Name, e.OrganizationId, e.Role))
            .ToList();

        return Ok(employees);
    }

    // Day 37: membership + a first granular RBAC rule. X-Organization-Id
    // (Day 35) answers "which tenant" — it says nothing about whether the
    // caller is allowed to create employees *within* that tenant. X-Employee-Id
    // is today's equally deliberate, equally honest simplification for
    // "who is calling": a plain, client-stated header, not a verified
    // identity. Only an Admin-role employee (looked up by that id) may create
    // new employees; a Member gets 403, matching StockPilot Day 25's
    // Admin/Employee distinction, but scoped per-tenant instead of system-wide.
    //
    // Day 123: identity from the token. Day 38's horizontal-escalation check
    // ("an Admin of organization 1 naming organization 2") is now enforced
    // by construction: the new employee's organization is the token's, so
    // there is no client-supplied organization left to compare against.
    [HttpPost]
    public async Task<ActionResult<EmployeeDto>> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var (actingEmployee, identityError) = await GetActingEmployeeAsync(cancellationToken);
        if (identityError is not null)
        {
            return identityError;
        }

        if (actingEmployee!.Role != EmployeeRole.Admin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only an Admin can create employees.");
        }

        var organizationId = actingEmployee.OrganizationId;

        // The cross-module orchestration (does this organization exist? if
        // so, create the employee) now lives entirely in
        // EmployeeApplicationService (Day 34) — this action's only job is
        // translating that plain result into an HTTP response. organizationId
        // now comes from the header, never from the request body (Day 35).
        var result = _employeeApplicationService.CreateEmployee(request.Name, organizationId, request.Password);
        if (!result.Succeeded)
        {
            return BadRequest(result.Error);
        }

        var dto = new EmployeeDto(result.Employee!.Id, result.Employee.Name, result.Employee.OrganizationId, result.Employee.Role);
        // No single-employee GetById action exists yet (out of today's scope,
        // which is the cross-module orchestration, not full Employees CRUD),
        // so there's no correct target for a Location header via
        // CreatedAtAction — 201 is returned directly instead.
        return StatusCode(StatusCodes.Status201Created, dto);
    }

    // Day 123: the caller, from the validated token — and still checked
    // against the database: the token may name an employee who was deleted
    // (401) or whose organization changed after login (403).
    private async Task<(EmployeeSummary? Employee, ActionResult? Error)> GetActingEmployeeAsync(CancellationToken cancellationToken)
    {
        var employeeId = User.GetEmployeeId();
        var organizationId = User.GetOrganizationId();
        if (employeeId is null || organizationId is null)
        {
            return (null, Unauthorized());
        }

        var employee = await _employeeDirectory.GetByIdAsync(employeeId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, Unauthorized());
        }

        if (employee.OrganizationId != organizationId)
        {
            return (null, StatusCode(StatusCodes.Status403Forbidden, "You can only act within your own organization."));
        }

        return (employee, null);
    }
}
