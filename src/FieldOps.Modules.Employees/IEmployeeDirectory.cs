namespace FieldOps.Modules.Employees;

// Deliberately does NOT validate that organizationId refers to a real
// Organization — this module has no way to check that (no reference to
// FieldOps.Modules.Organizations at all) and isn't meant to. That validation
// is the host's job (see FieldOps.Api's EmployeesController and ADR 0002),
// performed BEFORE Create is ever called.
public interface IEmployeeDirectory
{
    // Day 123: replaces GetAll(), whose only caller filtered every
    // organization's employees in memory (Day 113's fetch-all pattern) —
    // the organization filter now runs in SQL.
    Task<IReadOnlyList<EmployeeSummary>> GetByOrganizationAsync(int organizationId, CancellationToken cancellationToken);
    EmployeeSummary? GetById(int id);

    // Day 118: every work-order request's membership check looks up the
    // acting employee; on the async read path that lookup must not block a
    // thread-pool thread (Day 117 measured thread-pool starvation).
    Task<EmployeeSummary?> GetByIdAsync(int id, CancellationToken cancellationToken);

    // Day 122: passwordHash is produced by the host; this module only stores
    // it. It is deliberately not part of EmployeeSummary, so a hash never
    // travels with ordinary employee data (lists, DTOs, logs).
    EmployeeSummary Create(string name, int organizationId, EmployeeRole role, string passwordHash);

    Task<string?> GetPasswordHashAsync(int id, CancellationToken cancellationToken);
}
