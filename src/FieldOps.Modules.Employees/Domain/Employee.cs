namespace FieldOps.Modules.Employees.Domain;

// OrganizationId is a plain int — NOT a reference to the Organizations
// module's Organization type. This module has no project reference to
// FieldOps.Modules.Organizations at all (see ADR 0002) and doesn't need one:
// it only ever stores and returns the identifier, the same way a foreign-key
// column in a database doesn't need to "know" about the table it points to.
internal class Employee
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int OrganizationId { get; set; }
    public EmployeeRole Role { get; set; }

    // Day 122 (SECURITY_REVIEW.md F2): the employee's password, hashed by the
    // host before it ever reaches this module — this module stores the hash
    // as an opaque string and never sees a plain password or knows the
    // hashing algorithm. Null means "cannot log in".
    public string? PasswordHash { get; set; }

    public Employee(string name, int organizationId, EmployeeRole role)
    {
        Name = name;
        OrganizationId = organizationId;
        Role = role;
    }
}
