using FieldOps.Modules.Employees.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Modules.Employees.Data;

// internal, replacing InMemoryEmployeeDirectory (Day 37) as
// IEmployeeDirectory's real implementation. Ids for newly-created employees
// (Create) come from SQL Server's own IDENTITY column, continuing after the
// 5 explicitly-seeded Ids (HasData) — same as EfOrganizationDirectory
// (Day 48), still deliberately synchronous.
internal class EfEmployeeDirectory : IEmployeeDirectory
{
    private readonly EmployeesDbContext _dbContext;

    public EfEmployeeDirectory(EmployeesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<EmployeeSummary>> GetByOrganizationAsync(int organizationId, CancellationToken cancellationToken)
    {
        return await _dbContext.Employees
            .Where(e => e.OrganizationId == organizationId)
            .OrderBy(e => e.Id)
            .Select(e => new EmployeeSummary(e.Id, e.Name, e.OrganizationId, e.Role))
            .ToListAsync(cancellationToken);
    }

    public EmployeeSummary? GetById(int id)
    {
        var employee = _dbContext.Employees.FirstOrDefault(e => e.Id == id);
        return employee is null ? null : new EmployeeSummary(employee.Id, employee.Name, employee.OrganizationId, employee.Role);
    }

    // Day 118: same query as GetById, awaited — the thread returns to the pool
    // while SQL Server answers.
    public async Task<EmployeeSummary?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var employee = await _dbContext.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        return employee is null ? null : new EmployeeSummary(employee.Id, employee.Name, employee.OrganizationId, employee.Role);
    }

    public async Task<string?> GetPasswordHashAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbContext.Employees
            .Where(e => e.Id == id)
            .Select(e => e.PasswordHash)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public EmployeeSummary Create(string name, int organizationId, EmployeeRole role, string passwordHash)
    {
        var employee = new Employee(name, organizationId, role) { PasswordHash = passwordHash };
        _dbContext.Employees.Add(employee);
        _dbContext.SaveChanges();
        return new EmployeeSummary(employee.Id, employee.Name, employee.OrganizationId, employee.Role);
    }
}
