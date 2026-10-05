using System.Net;
using System.Net.Http.Json;

namespace FieldOps.Api.Tests;

// Unlike EmployeeApplicationServiceTests (Day 34), which call the service
// directly with zero HTTP involved, these tests go through a real HttpClient
// against a real running copy of the app — the only way to actually prove
// [FromHeader]'s behavior, since that's a middleware/model-binding concern
// EmployeeApplicationServiceTests structurally cannot see.
//
// Note: IEmployeeDirectory is registered Singleton, so its in-memory state
// is SHARED across every test in this class (one factory instance backs the
// whole class). IOrganizationDirectory (Day 48) is now EF-backed against a
// real, disposable Testcontainers SQL Server (FieldOpsApiFactory) — also
// shared across the class, for a different reason (a real database, not
// shared memory). Each test uses a unique employee name to avoid any test
// depending on another's leftover data — the same discipline StockPilot's
// Day 27 unique-SKU convention used.
// Day 121: the API now requires a bearer token everywhere (fallback policy),
// but EmployeesController still reads identity from X-Organization-Id /
// X-Employee-Id until its own conversion. So every client authenticates
// (as employee 1 — the token isn't consulted by this controller yet) and the
// header-based scenarios below keep testing exactly what they did before.
public class EmployeesAuthorizationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public EmployeesAuthorizationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_NoOrganizationHeader_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);

        var response = await client.GetAsync("/api/employees");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Day 39: this was the exact exploit live-proven before the fix existed —
    // GetAll required X-Organization-Id but never asked who was asking at
    // all. With no X-Employee-Id whatsoever (not even a fake one, unlike
    // Create's Day 38 exploit), anyone could read any organization's full
    // employee list.
    [Fact]
    public async Task GetAll_NoEmployeeHeader_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");

        var response = await client.GetAsync("/api/employees");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ByEmployeeFromAnotherOrganization_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "1"); // seeded Org1 Admin, targeting Org 2's list

        var response = await client.GetAsync("/api/employees");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_NoOrganizationHeader_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Should Never Exist", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ByAdmin_ReturnsCreated()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "1"); // seeded Org1 Admin

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Should Never Exist", Password = TestAuth.DemoPassword });
        var dto = await response.Content.ReadFromJsonAsync<EmployeeDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, dto!.OrganizationId);
    }

    [Fact]
    public async Task Create_ByMember_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "2"); // seeded Org1 Member

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Should Never Exist", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Day 38: this is the exact exploit that was live-proven before the fix
    // existed — Org 1's Admin (id=1) targeting Org 2 via X-Organization-Id.
    // Role alone (Admin) was never enough; the acting employee's OWN
    // organization must match the one they're trying to act within.
    [Fact]
    public async Task Create_ByAdminFromAnotherOrganization_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "1"); // seeded Org1 Admin, targeting Org 2

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Cross-Org Injected Employee", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_NonExistentOrganization_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1);
        client.DefaultRequestHeaders.Add("X-Organization-Id", "999");
        // seeded "Orphaned Admin" (id=5) whose own OrganizationId is also 999 —
        // needed since Day 38: an Admin whose own org doesn't match the
        // target gets 403 before this org-existence check is ever reached.
        client.DefaultRequestHeaders.Add("X-Employee-Id", "5");

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Ghost Employee", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ScopedToOrganization_NeverReturnsAnotherOrganizationsEmployees()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1);
        org1Client.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        org1Client.DefaultRequestHeaders.Add("X-Employee-Id", "1"); // seeded Org1 Admin
        var org2Client = _factory.CreateClient();
        await org2Client.AuthenticateAsAsync(1);
        org2Client.DefaultRequestHeaders.Add("X-Organization-Id", "2");
        org2Client.DefaultRequestHeaders.Add("X-Employee-Id", "3"); // seeded Org2 Admin

        var createResponse = await org1Client.PostAsJsonAsync("/api/employees", new { Name = $"Org1-Only-{uniqueSuffix}", Password = TestAuth.DemoPassword });
        createResponse.EnsureSuccessStatusCode();

        var org1Employees = await org1Client.GetFromJsonAsync<List<EmployeeDto>>("/api/employees");
        var org2Employees = await org2Client.GetFromJsonAsync<List<EmployeeDto>>("/api/employees");

        Assert.Contains(org1Employees!, e => e.Name == $"Org1-Only-{uniqueSuffix}");
        Assert.DoesNotContain(org2Employees!, e => e.Name == $"Org1-Only-{uniqueSuffix}");
    }

    private record EmployeeDto(int Id, string Name, int OrganizationId);
}
