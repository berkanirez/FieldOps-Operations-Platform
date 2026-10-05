using System.Net;
using System.Net.Http.Json;

namespace FieldOps.Api.Tests;

// Day 115: the work-order list was unbounded — Day 114 measured how an
// unbounded list makes a single cached plan wrong for large organizations.
// The list is now paged; and because the Angular detail page found a work
// order by filtering the full list, a get-by-id endpoint is added too.
// This class gets its own factory, so its database starts empty.
public class WorkOrdersPaginationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public WorkOrdersPaginationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_WithPageSize_ReturnsThatPageInIdOrder()
    {
        // Organization 1's seeded Admin (employee 1).
        var client = await CreateClientAsync(1);
        for (var i = 1; i <= 3; i++)
        {
            var created = await client.PostAsJsonAsync("/api/workorders", new { Title = $"Pagination check {i}" });
            created.EnsureSuccessStatusCode();
        }

        // Other tests in this class share the database and may add work
        // orders in any order, so the expectation is derived from the full
        // ordered list instead of assuming which ids exist.
        var all = (await client.GetFromJsonAsync<List<WorkOrderResponse>>("/api/workorders?page=1&pageSize=100"))!
            .Select(w => w.Id).ToList();
        var firstPage = await client.GetFromJsonAsync<List<WorkOrderResponse>>("/api/workorders?page=1&pageSize=2");
        var secondPage = await client.GetFromJsonAsync<List<WorkOrderResponse>>("/api/workorders?page=2&pageSize=2");

        Assert.True(all.Count >= 3);
        Assert.Equal(all.Order(), all);
        Assert.Equal(all.Take(2), firstPage!.Select(w => w.Id));
        Assert.Equal(all.Skip(2).Take(2), secondPage!.Select(w => w.Id));
    }

    [Theory]
    [InlineData("page=0&pageSize=10")]
    [InlineData("page=1&pageSize=0")]
    [InlineData("page=1&pageSize=101")]
    public async Task GetAll_WithOutOfRangePaging_Returns400ProblemDetails(string query)
    {
        var client = await CreateClientAsync(1);

        var response = await client.GetAsync($"/api/workorders?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetById_OwnOrganization_Returns200()
    {
        var client = await CreateClientAsync(1);
        var created = await client.PostAsJsonAsync("/api/workorders", new { Title = "Get by id check" });
        var id = (await created.Content.ReadFromJsonAsync<WorkOrderResponse>())!.Id;

        var response = await client.GetAsync($"/api/workorders/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrder = await response.Content.ReadFromJsonAsync<WorkOrderResponse>();
        Assert.Equal(id, workOrder!.Id);
        Assert.Equal("Get by id check", workOrder.Title);
    }

    [Fact]
    public async Task GetById_OtherOrganizationsWorkOrder_Returns404()
    {
        var organizationOne = await CreateClientAsync(1);
        var created = await organizationOne.PostAsJsonAsync("/api/workorders", new { Title = "Belongs to organization 1" });
        var id = (await created.Content.ReadFromJsonAsync<WorkOrderResponse>())!.Id;

        // Organization 2's seeded Admin (employee 3) asks for organization 1's
        // work order by id: 404, not 403 — a 403 would confirm it exists.
        var organizationTwo = await CreateClientAsync(3);
        var response = await organizationTwo.GetAsync($"/api/workorders/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Same id, owning organization: 200 — proves the 404 above comes from
        // the tenant check, not from the route not existing.
        var ownResponse = await organizationOne.GetAsync($"/api/workorders/{id}");
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
    }

    // Day 121: identity comes from a real login token, not headers.
    private async Task<HttpClient> CreateClientAsync(int employeeId)
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(employeeId);
        return client;
    }

    private record WorkOrderResponse(int Id, string Title, int OrganizationId);
}
