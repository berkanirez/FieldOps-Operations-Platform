using System.Net;
using System.Net.Http.Json;

namespace FieldOps.Api.Tests;

// Day 123 (SECURITY_REVIEW.md F13): GET /api/organizations used to list every
// organization to any caller. Now a caller sees only the organization in
// their token; another organization's id is a 404, not a 403.
public class OrganizationsAuthorizationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public OrganizationsAuthorizationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_ReturnsOnlyTheCallersOrganization()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(3); // seeded Org2 Admin

        var organizations = await client.GetFromJsonAsync<List<OrganizationDto>>("/api/organizations");

        var organization = Assert.Single(organizations!);
        Assert.Equal(2, organization.Id);
    }

    [Fact]
    public async Task GetById_OwnOrganization_ReturnsOk()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin

        var response = await client.GetAsync("/api/organizations/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetById_AnotherOrganization_ReturnsNotFound()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin

        var response = await client.GetAsync("/api/organizations/2");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private record OrganizationDto(int Id, string Name);
}
