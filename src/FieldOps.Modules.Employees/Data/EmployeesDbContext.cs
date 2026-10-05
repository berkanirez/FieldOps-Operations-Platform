using FieldOps.Modules.Employees.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Modules.Employees.Data;

// internal — same ADR 0003 pattern as OrganizationsDbContext (Day 48):
// this module owns its own database, the host never touches this type.
internal class EmployeesDbContext : DbContext
{
    public EmployeesDbContext(DbContextOptions<EmployeesDbContext> options) : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();

    private const string DemoPasswordHash =
        "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Employee>(entity =>
        {
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);

            // Day 122: PBKDF2 hashes from ASP.NET Core's PasswordHasher are
            // well under 200 characters.
            entity.Property(e => e.PasswordHash).HasMaxLength(200);

            // Same five seeded employees, same Ids, as InMemoryEmployeeDirectory
            // (Day 37) — WorkOrders' tests still assume Employee Ids 1-5 mean
            // exactly what they meant before.
            //
            // Day 122: every demo employee gets the same LOCAL-DEMO password,
            // "FieldOps-Demo-2026!" (documented in the README). The hash is a
            // constant generated once: PasswordHasher salts randomly, so
            // calling it here would produce a new value on every model build
            // and make EF Core think the seed data changed each time.
            entity.HasData(
                new Employee("Org1 Admin", organizationId: 1, EmployeeRole.Admin) { Id = 1, PasswordHash = DemoPasswordHash },
                new Employee("Org1 Member", organizationId: 1, EmployeeRole.Member) { Id = 2, PasswordHash = DemoPasswordHash },
                new Employee("Org2 Admin", organizationId: 2, EmployeeRole.Admin) { Id = 3, PasswordHash = DemoPasswordHash },
                new Employee("Org2 Member", organizationId: 2, EmployeeRole.Member) { Id = 4, PasswordHash = DemoPasswordHash },
                new Employee("Orphaned Admin", organizationId: 999, EmployeeRole.Admin) { Id = 5, PasswordHash = DemoPasswordHash }
            );
        });
    }
}
