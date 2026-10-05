using FieldOps.Api.Application;
using FieldOps.Modules.Employees;
using FieldOps.Modules.Organizations;

namespace FieldOps.Api.Tests;

// EmployeeApplicationService (Day 34) has zero ASP.NET Core dependency —
// these tests prove exactly that: no ControllerBase, no ActionResult, no
// HTTP pipeline anywhere, just plain C# objects. Hand-written fakes are used
// (not Moq) since both interfaces are small enough that a real, if minimal,
// implementation is simpler than a mock setup — the same reasoning
// InMemoryProductStore used in StockPilot.
public class EmployeeApplicationServiceTests
{
    [Fact]
    public void CreateEmployee_ExistingOrganization_ReturnsSuccessWithEmployee()
    {
        var employeeDirectory = new FakeEmployeeDirectory();
        var hasher = new EmployeePasswordHasher();
        var service = new EmployeeApplicationService(
            employeeDirectory,
            new FakeOrganizationDirectory(existingOrganizationId: 1),
            hasher);

        var result = service.CreateEmployee("Jane Tech", 1, "a-long-enough-password");

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal("Jane Tech", result.Employee!.Name);
        Assert.Equal(1, result.Employee.OrganizationId);

        // Day 122: the module receives a hash, never the password itself.
        var storedHash = employeeDirectory.PasswordHashes[result.Employee.Id];
        Assert.NotEqual("a-long-enough-password", storedHash);
        Assert.True(hasher.Verify(storedHash, "a-long-enough-password"));
    }

    [Fact]
    public void CreateEmployee_MissingOrganization_ReturnsFailureAndCreatesNoEmployee()
    {
        var employeeDirectory = new FakeEmployeeDirectory();
        var service = new EmployeeApplicationService(
            employeeDirectory,
            new FakeOrganizationDirectory(existingOrganizationId: 1),
            new EmployeePasswordHasher());

        var result = service.CreateEmployee("Ghost Employee", 999, "a-long-enough-password");

        Assert.False(result.Succeeded);
        Assert.Null(result.Employee);
        Assert.Equal("Organization 999 does not exist.", result.Error);
        Assert.Empty(employeeDirectory.GetAll());
    }

    private class FakeOrganizationDirectory : IOrganizationDirectory
    {
        private readonly int _existingOrganizationId;

        public FakeOrganizationDirectory(int existingOrganizationId)
        {
            _existingOrganizationId = existingOrganizationId;
        }

        public IReadOnlyList<OrganizationSummary> GetAll() => new List<OrganizationSummary>();

        public OrganizationSummary? GetById(int id) =>
            id == _existingOrganizationId ? new OrganizationSummary(id, "Fake Org") : null;
    }

    private class FakeEmployeeDirectory : IEmployeeDirectory
    {
        private readonly List<EmployeeSummary> _employees = new();
        private int _nextId = 1;

        public IReadOnlyList<EmployeeSummary> GetAll() => _employees;

        public EmployeeSummary? GetById(int id) => _employees.FirstOrDefault(e => e.Id == id);

        public Task<EmployeeSummary?> GetByIdAsync(int id, CancellationToken cancellationToken) => Task.FromResult(GetById(id));

        public Dictionary<int, string> PasswordHashes { get; } = new();

        public EmployeeSummary Create(string name, int organizationId, EmployeeRole role, string passwordHash)
        {
            var summary = new EmployeeSummary(_nextId++, name, organizationId, role);
            _employees.Add(summary);
            PasswordHashes[summary.Id] = passwordHash;
            return summary;
        }

        public Task<string?> GetPasswordHashAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(PasswordHashes.TryGetValue(id, out var hash) ? hash : null);
    }
}
