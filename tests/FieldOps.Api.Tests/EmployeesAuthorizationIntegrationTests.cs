using System.Net;
using System.Net.Http.Json;

namespace FieldOps.Api.Tests;

// Unlike EmployeeApplicationServiceTests (Day 34), which call the service
// directly with zero HTTP involved, these tests go through a real HttpClient
// against a real running copy of the app.
//
// IOrganizationDirectory (Day 48) is EF-backed against a real, disposable
// Testcontainers SQL Server (FieldOpsApiFactory), shared across the class.
// Each test uses a unique employee name to avoid depending on another's
// leftover data — the same discipline StockPilot's Day 27 unique-SKU
// convention used.
//
// Day 123: EmployeesController takes the caller's identity from the token,
// like WorkOrdersController since Day 121. The Day 38 and Day 39 exploits
// (an Admin of organization 1 targeting organization 2; reading a list with
// no identity at all) are kept as regression tests in their token form.
public class EmployeesAuthorizationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public EmployeesAuthorizationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/employees");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Day 39's exploit: an organization's full employee list read with no
    // verified identity — headers alone are no longer an identity.
    [Fact]
    public async Task GetAll_IdentityHeadersWithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "3");

        var response = await client.GetAsync("/api/employees");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ForgedOrganizationHeader_IsIgnored()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");

        var employees = await client.GetFromJsonAsync<List<EmployeeDto>>("/api/employees");

        Assert.NotEmpty(employees!);
        Assert.All(employees!, e => Assert.Equal(1, e.OrganizationId));
    }

    [Fact]
    public async Task Create_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Should Never Exist", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_ByAdmin_ReturnsCreated()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = $"New-{Guid.NewGuid():N}", Password = TestAuth.DemoPassword });
        var dto = await response.Content.ReadFromJsonAsync<EmployeeDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, dto!.OrganizationId);
    }

    [Fact]
    public async Task Create_ByMember_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(2); // seeded Org1 Member

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Should Never Exist", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Day 38's exploit — organization 1's Admin creating an employee in
    // organization 2 by naming it — now has nothing to name: the new
    // employee always lands in the token's organization.
    [Fact]
    public async Task Create_ForgedOrganizationHeader_CreatesInTokensOrganization()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = $"Cross-Org-Attempt-{Guid.NewGuid():N}", Password = TestAuth.DemoPassword });
        var dto = await response.Content.ReadFromJsonAsync<EmployeeDto>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, dto!.OrganizationId);
    }

    [Fact]
    public async Task Create_NonExistentOrganization_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        // seeded "Orphaned Admin" (id=5), whose own OrganizationId is 999 —
        // an organization that doesn't exist.
        await client.AuthenticateAsAsync(5);

        var response = await client.PostAsJsonAsync("/api/employees", new { Name = "Ghost Employee", Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_ScopedToOrganization_NeverReturnsAnotherOrganizationsEmployees()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1); // seeded Org1 Admin
        var org2Client = _factory.CreateClient();
        await org2Client.AuthenticateAsAsync(3); // seeded Org2 Admin

        var createResponse = await org1Client.PostAsJsonAsync("/api/employees", new { Name = $"Org1-Only-{uniqueSuffix}", Password = TestAuth.DemoPassword });
        createResponse.EnsureSuccessStatusCode();

        var org1Employees = await org1Client.GetFromJsonAsync<List<EmployeeDto>>("/api/employees");
        var org2Employees = await org2Client.GetFromJsonAsync<List<EmployeeDto>>("/api/employees");

        Assert.Contains(org1Employees!, e => e.Name == $"Org1-Only-{uniqueSuffix}");
        Assert.DoesNotContain(org2Employees!, e => e.Name == $"Org1-Only-{uniqueSuffix}");
    }

    private record EmployeeDto(int Id, string Name, int OrganizationId);
}
