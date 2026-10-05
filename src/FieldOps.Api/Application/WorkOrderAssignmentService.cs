using FieldOps.Modules.Employees;
using FieldOps.Modules.WorkOrders;

namespace FieldOps.Api.Application;

// Same justification as EmployeeApplicationService (Day 34): real
// cross-module coordination (needs both IWorkOrderDirectory and
// IEmployeeDirectory) plus a genuine business rule (the assignee must
// belong to the work order's own organization) — not extracted just
// because it "sounds like a service."
public class WorkOrderAssignmentService
{
    private readonly IWorkOrderDirectory _workOrderDirectory;
    private readonly IEmployeeDirectory _employeeDirectory;

    public WorkOrderAssignmentService(IWorkOrderDirectory workOrderDirectory, IEmployeeDirectory employeeDirectory)
    {
        _workOrderDirectory = workOrderDirectory;
        _employeeDirectory = employeeDirectory;
    }

    // Day 119: async — Assign is part of the write flow the mixed load test
    // exercises. An async method can't have an out parameter, so the lookups
    // happen here and the shared checks live in ValidateWorkOrderAndEmployee's
    // pure decision part (CheckWorkOrderAndEmployee), used by both paths.
    public async Task<WorkOrderAssignmentResult> AssignWorkOrderAsync(int workOrderId, int employeeId, int callerOrganizationId, CancellationToken cancellationToken)
    {
        var workOrder = await _workOrderDirectory.GetByIdAsync(workOrderId, cancellationToken);
        var employee = workOrder is null ? null : await _employeeDirectory.GetByIdAsync(employeeId, cancellationToken);
        var validationError = CheckWorkOrderAndEmployee(workOrderId, employeeId, callerOrganizationId, workOrder, employee);
        if (validationError is not null)
        {
            return validationError;
        }

        if (workOrder!.Status != WorkOrderStatus.Open)
        {
            return WorkOrderAssignmentResult.Failure($"Work order {workOrderId} is not open for assignment.");
        }

        var assigned = await _workOrderDirectory.AssignAsync(workOrderId, employeeId, cancellationToken);
        return WorkOrderAssignmentResult.Success(assigned!);
    }

    // Day 43: shares AssignWorkOrder's exact "does the work order/employee
    // exist and match" checks via ValidateWorkOrderAndEmployee — unlike
    // Day 39's EmployeesController.GetAll/Create (left duplicated, since
    // Create had an extra check making the two non-identical), these two
    // validations are genuinely byte-for-byte the same here, only the
    // status precondition and the final mutation differ.
    public WorkOrderAssignmentResult ReassignWorkOrder(int workOrderId, int newEmployeeId, int callerOrganizationId)
    {
        var validationError = ValidateWorkOrderAndEmployee(workOrderId, newEmployeeId, callerOrganizationId, out var workOrder);
        if (validationError is not null)
        {
            return validationError;
        }

        if (workOrder!.Status != WorkOrderStatus.Assigned && workOrder.Status != WorkOrderStatus.InProgress)
        {
            return WorkOrderAssignmentResult.Failure($"Work order {workOrderId} is not currently assigned or in progress, so it cannot be reassigned.");
        }

        // Day 44 independent-task fix: "reassigning" a work order to the
        // employee it's already assigned to is a meaningless no-op that
        // nothing was rejecting — caught by Berkan reading the code, not
        // found by any test.
        if (newEmployeeId == workOrder.AssignedEmployeeId)
        {
            return WorkOrderAssignmentResult.Failure($"Work order {workOrderId} is already assigned to employee {newEmployeeId}.");
        }

        var reassigned = _workOrderDirectory.Reassign(workOrderId, newEmployeeId);
        return WorkOrderAssignmentResult.Success(reassigned!);
    }

    // A work order that doesn't exist and one that exists but belongs to a
    // different organization return the IDENTICAL message on purpose —
    // applying Day 37/38's lesson: a caller outside a work order's
    // organization should never be able to tell the two cases apart, which
    // would otherwise let them probe for valid work order ids belonging to
    // other tenants. Same information-hiding principle for the employee
    // lookup, applied consistently since Day 41's independent-task fix.
    private WorkOrderAssignmentResult? ValidateWorkOrderAndEmployee(int workOrderId, int employeeId, int callerOrganizationId, out WorkOrderSummary? workOrder)
    {
        // The caller only uses workOrder when no error is returned.
        workOrder = _workOrderDirectory.GetById(workOrderId);
        var employee = workOrder is null ? null : _employeeDirectory.GetById(employeeId);
        return CheckWorkOrderAndEmployee(workOrderId, employeeId, callerOrganizationId, workOrder, employee);
    }

    // Day 119: the rules themselves, shared by the sync (Reassign) and async
    // (Assign) paths so they can never drift apart.
    private static WorkOrderAssignmentResult? CheckWorkOrderAndEmployee(int workOrderId, int employeeId, int callerOrganizationId, WorkOrderSummary? workOrder, EmployeeSummary? employee)
    {
        if (workOrder is null || workOrder.OrganizationId != callerOrganizationId)
        {
            return WorkOrderAssignmentResult.Failure($"Work order {workOrderId} does not exist.");
        }

        if (employee is null || employee.OrganizationId != workOrder.OrganizationId)
        {
            return WorkOrderAssignmentResult.Failure($"Employee {employeeId} does not exist.");
        }

        return null;
    }
}
