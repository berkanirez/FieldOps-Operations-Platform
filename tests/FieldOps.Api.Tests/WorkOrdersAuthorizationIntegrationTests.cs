using System.Net;
using System.Net.Http.Json;
using FieldOps.Api.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Api.Tests;

// Day 40: WorkOrdersController applies the same membership pattern
// EmployeesController arrived at only after three live-found vulnerabilities
// (Days 35, 38, 39) — here it's correct from the start. These tests prove
// that directly, rather than proving a fix for something that was broken.
//
// Day 48: uses FieldOpsApiFactory (a real, disposable Testcontainers SQL
// Server, mirroring StockPilot Day 28) instead of a plain
// WebApplicationFactory<Program> — Organizations is now EF-backed, and
// employee-creation calls in these tests go through EmployeeApplicationService,
// which checks organization existence via that real database.
public class WorkOrdersAuthorizationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public WorkOrdersAuthorizationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAll_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/workorders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Day 121 (SECURITY_REVIEW.md F1, probe P1): identity headers alone are no
    // longer an identity. Before today this exact request — organization 1's
    // Admin claimed purely in headers — returned 200.
    [Fact]
    public async Task GetAll_IdentityHeadersWithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "1");

        var response = await client.GetAsync("/api/workorders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Day 121 (probe P3): a forged organization header is ignored — the
    // tenant comes from the token. Previously the same header switched the
    // request to organization 2 (and was then refused with 403).
    [Fact]
    public async Task GetAll_ForgedOrganizationHeader_IsIgnored()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");

        var response = await client.GetAsync("/api/workorders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var workOrders = await response.Content.ReadFromJsonAsync<List<WorkOrderDto>>();
        Assert.All(workOrders!, w => Assert.Equal(1, w.OrganizationId));
    }

    [Fact]
    public async Task GetAll_TokenSignedWithAnotherKey_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", ForgeToken(signingKey: "an-attackers-own-key-that-is-at-least-32-bytes-long"));

        var response = await client.GetAsync("/api/workorders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_MalformedToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/workorders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A structurally valid token claiming organization 1's Admin, signed with
    // a key the API doesn't trust — what an attacker without the real key
    // could produce.
    private static string ForgeToken(string signingKey)
    {
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "FieldOps.Api",
            audience: "FieldOps.Api",
            claims: [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"),
                new System.Security.Claims.Claim("organizationId", "1"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    // Day 61: a coverage audit found that Create shares ValidateMembership
    // with GetAll, but — unlike GetAll — had never gotten its own dedicated
    // tests for the same checks. Same header-missing/cross-org scenarios,
    // proven directly against Create this time, not inferred from GetAll's
    // coverage of a shared helper.
    [Fact]
    public async Task Create_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workorders", new { Title = "Should-Never-Be-Created" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Day 121 (probe P1/P3): organization 1's Admin sending organization 2's
    // header creates the work order in organization 1 — the token decides.
    [Fact]
    public async Task Create_ForgedOrganizationHeader_CreatesInTokensOrganization()
    {
        var client = _factory.CreateClient();
        await client.AuthenticateAsAsync(1); // seeded Org1 Admin
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");

        var response = await client.PostAsJsonAsync("/api/workorders", new { Title = "Forged-Header-Check" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<WorkOrderDto>();
        Assert.Equal(1, dto!.OrganizationId);
    }

    [Fact]
    public async Task Create_ThenGetAll_ScopedToOwnOrganization()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1); // seeded Org1 Admin
        var org2Client = _factory.CreateClient();
        await org2Client.AuthenticateAsAsync(3); // seeded Org2 Admin

        var createResponse = await org1Client.PostAsJsonAsync("/api/workorders", new { Title = $"Org1-Only-{uniqueSuffix}" });
        var dto = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(1, dto!.OrganizationId);

        var org1WorkOrders = await org1Client.GetFromJsonAsync<List<WorkOrderDto>>("/api/workorders");
        var org2WorkOrders = await org2Client.GetFromJsonAsync<List<WorkOrderDto>>("/api/workorders");

        Assert.Contains(org1WorkOrders!, w => w.Title == $"Org1-Only-{uniqueSuffix}");
        Assert.DoesNotContain(org2WorkOrders!, w => w.Title == $"Org1-Only-{uniqueSuffix}");
    }

    // Day 62: audit continuation — Assign shares ValidateMembership with
    // GetAll/Create but never had its own dedicated test proving the check
    // is actually still wired up here. A valid body is sent (only the
    // headers are omitted) so a 400 can only mean ValidateMembership fired,
    // never [ApiController]'s own unrelated model-validation.
    [Fact]
    public async Task Assign_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workorders/999999/assign", new { EmployeeId = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Assign_ByAdmin_TransitionsToAssigned()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1); // seeded Org1 Admin

        var createResponse = await org1Client.PostAsJsonAsync("/api/workorders", new { Title = $"Assign-Me-{uniqueSuffix}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var assignResponse = await org1Client.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 2 }); // seeded Org1 Member
        var assigned = await assignResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);
        Assert.Equal(1, assigned!.Status); // WorkOrderStatus.Assigned == 1
        Assert.Equal(2, assigned.AssignedEmployeeId);
    }

    [Fact]
    public async Task Assign_ByMember_ReturnsForbidden()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Member-Cannot-Assign-{uniqueSuffix}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2); // seeded Org1 Member

        var assignResponse = await org1MemberClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, assignResponse.StatusCode);
    }

    [Fact]
    public async Task Assign_AlreadyAssigned_ReturnsBadRequest()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1);

        var createResponse = await org1Client.PostAsJsonAsync("/api/workorders", new { Title = $"Only-Once-{uniqueSuffix}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var firstAssign = await org1Client.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 2 });
        firstAssign.EnsureSuccessStatusCode();

        var secondAssign = await org1Client.PostAsJsonAsync($"/api/workorders/{created.Id}/assign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, secondAssign.StatusCode);
    }

    [Fact]
    public async Task Assign_ToEmployeeFromAnotherOrganization_ReturnsBadRequest()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1);

        var createResponse = await org1Client.PostAsJsonAsync("/api/workorders", new { Title = $"Cross-Org-Assignee-{uniqueSuffix}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var assignResponse = await org1Client.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 4 }); // seeded Org2 Member

        Assert.Equal(HttpStatusCode.BadRequest, assignResponse.StatusCode);
    }

    // Day 41: applies Day 37/38's information-disclosure lesson from the
    // start — an Org 2 Admin targeting Org 1's work order should see the
    // exact same "does not exist" outcome as a genuinely missing id, never
    // a distinguishable error revealing that work order 1 belongs to someone
    // else's tenant.
    [Fact]
    public async Task Assign_WorkOrderFromAnotherOrganization_ReturnsBadRequest()
    {
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var org1Client = _factory.CreateClient();
        await org1Client.AuthenticateAsAsync(1);

        var createResponse = await org1Client.PostAsJsonAsync("/api/workorders", new { Title = $"Org1-Private-{uniqueSuffix}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var org2Client = _factory.CreateClient();
        await org2Client.AuthenticateAsAsync(3); // seeded Org2 Admin

        // Deliberately targets employee id 2 — Org 1's OWN Member. If the
        // work-order-organization check were missing, this would otherwise
        // succeed (employee 2 genuinely belongs to Org 1, so the separate
        // employee-organization check alone wouldn't catch it), which is
        // exactly what makes this test an isolated proof of THAT check,
        // not an accidental pass via a different one.
        var assignResponse = await org2Client.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, assignResponse.StatusCode);
    }

    // Day 42: ownership-based authorization — a work order created and
    // assigned by Org1's Admin (id=1) to Org1's Member (id=2); only that
    // Member, not even the Admin who assigned it, may start/complete it.
    private async Task<WorkOrderDto> CreateAndAssignWorkOrderAsync(HttpClient adminClient, string title, int assigneeEmployeeId)
    {
        var createResponse = await adminClient.PostAsJsonAsync("/api/workorders", new { Title = title });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var assignResponse = await adminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = assigneeEmployeeId });
        return (await assignResponse.Content.ReadFromJsonAsync<WorkOrderDto>())!;
    }

    // Day 62: audit continuation — same reasoning as Assign's new test
    // above. Start takes no body, so nothing else could produce a 400 here
    // besides ValidateMembership.
    [Fact]
    public async Task Start_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/workorders/999999/start", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Start_ByAssignedEmployee_TransitionsToInProgress()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Start-Me-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2); // the assignee

        var startResponse = await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);
        var started = await startResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        Assert.Equal(2, started!.Status); // WorkOrderStatus.InProgress == 2
    }

    [Fact]
    public async Task Start_ByAdminWhoIsNotTheAssignee_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Admin-Cannot-Start-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        // The Admin assigned this work order but wasn't assigned it
        // themselves — ownership, not role, decides who may start it.
        var startResponse = await org1AdminClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);

        Assert.Equal(HttpStatusCode.Forbidden, startResponse.StatusCode);
    }

    // Named ReturnsForbidden, not ReturnsBadRequest: the ownership check
    // (AssignedEmployeeId != actingEmployeeId) runs BEFORE the state check,
    // and a null AssignedEmployeeId can never equal a real employee id — so
    // an unassigned work order always fails ownership first, no matter who
    // asks. The "must be Assigned to start" state-invariant path is
    // therefore unreachable for a genuinely unassigned work order; it only
    // ever fires for one that's already past Assigned (e.g. already
    // InProgress), which Day 43+ may want a dedicated test for.
    [Fact]
    public async Task Start_OnUnassignedWorkOrder_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Still-Open-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Nobody is assigned yet, so AssignedEmployeeId is null — even the
        // Admin who created it isn't "the assignee" of a null assignment.
        var startResponse = await org1AdminClient.PostAsync($"/api/workorders/{created!.Id}/start", null);

        Assert.Equal(HttpStatusCode.Forbidden, startResponse.StatusCode);
    }

    // Day 62: audit continuation — same reasoning as Assign/Start above.
    [Fact]
    public async Task Complete_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/workorders/999999/complete", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Complete_ByAssignedEmployee_TransitionsToCompleted()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Complete-Me-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);

        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);
        var completeResponse = await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/complete", null);
        var completed = await completeResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        Assert.Equal(3, completed!.Status); // WorkOrderStatus.Completed == 3
    }

    [Fact]
    public async Task Complete_BeforeStart_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Skip-Start-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);

        // Still Assigned, never started — completing straight from Assigned
        // skips a lifecycle step and must be rejected.
        var completeResponse = await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/complete", null);

        Assert.Equal(HttpStatusCode.BadRequest, completeResponse.StatusCode);
    }

    // Day 43: creates a genuinely new Org1 employee via the Employees API,
    // so reassignment tests have a second real, valid target within Org1
    // without depending on more seed data than already exists.
    //
    // Day 121: EmployeesController still reads identity from headers until
    // its own conversion, so this one request carries them alongside the
    // client's bearer token (the work-order endpoints ignore headers now).
    private static async Task<int> CreateOrg1EmployeeAsync(HttpClient org1AdminClient, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/employees")
        {
            Content = JsonContent.Create(new { Name = name })
        };
        request.Headers.Add("X-Organization-Id", "1");
        request.Headers.Add("X-Employee-Id", "1");
        var response = await org1AdminClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmployeeDto>();
        return body!.Id;
    }

    // Day 62: audit continuation — same reasoning as Assign's new test.
    [Fact]
    public async Task Reassign_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workorders/999999/reassign", new { EmployeeId = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reassign_WhileAssigned_ChangesAssigneeWithoutChangingStatus()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Reassign-Me-{Guid.NewGuid():N}", assigneeEmployeeId: 2);
        var newEmployeeId = await CreateOrg1EmployeeAsync(org1AdminClient, $"Cover-{Guid.NewGuid():N}");

        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = newEmployeeId });
        var reassigned = await reassignResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, reassignResponse.StatusCode);
        Assert.Equal(1, reassigned!.Status); // still WorkOrderStatus.Assigned
        Assert.Equal(newEmployeeId, reassigned.AssignedEmployeeId);
    }

    [Fact]
    public async Task Reassign_WhileInProgress_Succeeds()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Reassign-InProgress-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);

        var newEmployeeId = await CreateOrg1EmployeeAsync(org1AdminClient, $"Takeover-{Guid.NewGuid():N}");
        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = newEmployeeId });
        var reassigned = await reassignResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, reassignResponse.StatusCode);
        Assert.Equal(2, reassigned!.Status); // still WorkOrderStatus.InProgress
    }

    [Fact]
    public async Task Reassign_OnOpenWorkOrder_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Never-Assigned-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Nothing to reassign — Reassign is for handing off already-assigned
        // work, not a substitute for the first Assign.
        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/reassign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, reassignResponse.StatusCode);
    }

    [Fact]
    public async Task Reassign_OnCompletedWorkOrder_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Finished-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);
        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/complete", null);

        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, reassignResponse.StatusCode);
    }

    // Day 44: employee 2 (the seeded Org1 Member) is no longer a valid
    // "any Member is blocked" example, since it's also this test's assignee
    // and Day 44 made the assignee a legitimate actor for their OWN work
    // order. A genuinely unrelated Member — neither Admin nor the assignee —
    // is created fresh so this test still proves what it claims to.
    [Fact]
    public async Task Reassign_ByUnrelatedMember_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Unrelated-Cannot-Reassign-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var unrelatedEmployeeId = await CreateOrg1EmployeeAsync(org1AdminClient, $"Bystander-{Guid.NewGuid():N}");
        var unrelatedClient = _factory.CreateClient();
        await unrelatedClient.AuthenticateAsAsync(unrelatedEmployeeId);

        var reassignResponse = await unrelatedClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, reassignResponse.StatusCode);
    }

    // Day 44: the actual new capability — the current assignee (not an
    // Admin) hands their own work order off to someone else.
    [Fact]
    public async Task Reassign_ByCurrentAssignee_Succeeds()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Self-Handoff-{Guid.NewGuid():N}", assigneeEmployeeId: 2);
        var newEmployeeId = await CreateOrg1EmployeeAsync(org1AdminClient, $"Covering-{Guid.NewGuid():N}");

        var assigneeClient = _factory.CreateClient();
        await assigneeClient.AuthenticateAsAsync(2); // the assignee itself, not an Admin

        var reassignResponse = await assigneeClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = newEmployeeId });
        var reassigned = await reassignResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, reassignResponse.StatusCode);
        Assert.Equal(newEmployeeId, reassigned!.AssignedEmployeeId);
    }

    [Fact]
    public async Task Reassign_ToEmployeeFromAnotherOrganization_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Cross-Org-Reassign-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = 4 }); // seeded Org2 Member

        Assert.Equal(HttpStatusCode.BadRequest, reassignResponse.StatusCode);
    }

    // Day 44 independent-task fix: caught by Berkan reading the code, not
    // by any test — "reassigning" to the same employee already assigned
    // was a meaningless no-op that nothing rejected.
    [Fact]
    public async Task Reassign_ToSameEmployeeAlreadyAssigned_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"No-Op-Reassign-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var reassignResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/reassign", new { EmployeeId = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, reassignResponse.StatusCode);
    }

    // Day 62: audit continuation — Unassign takes no body, so nothing else
    // could produce a 400 here besides ValidateMembership.
    [Fact]
    public async Task Unassign_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/workorders/999999/unassign", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unassign_ByAdmin_ReturnsToOpenWithNoAssignee()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Unassign-By-Admin-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var unassignResponse = await org1AdminClient.PostAsync($"/api/workorders/{assigned.Id}/unassign", null);
        var unassigned = await unassignResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, unassignResponse.StatusCode);
        Assert.Equal(0, unassigned!.Status); // WorkOrderStatus.Open == 0
        Assert.Null(unassigned.AssignedEmployeeId);
    }

    [Fact]
    public async Task Unassign_ByCurrentAssignee_Succeeds()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Unassign-By-Self-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var assigneeClient = _factory.CreateClient();
        await assigneeClient.AuthenticateAsAsync(2); // the assignee, not an Admin

        var unassignResponse = await assigneeClient.PostAsync($"/api/workorders/{assigned.Id}/unassign", null);

        Assert.Equal(HttpStatusCode.OK, unassignResponse.StatusCode);
    }

    [Fact]
    public async Task Unassign_ByUnrelatedMember_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Unassign-Unrelated-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var unrelatedEmployeeId = await CreateOrg1EmployeeAsync(org1AdminClient, $"Bystander-{Guid.NewGuid():N}");
        var unrelatedClient = _factory.CreateClient();
        await unrelatedClient.AuthenticateAsAsync(unrelatedEmployeeId);

        var unassignResponse = await unrelatedClient.PostAsync($"/api/workorders/{assigned.Id}/unassign", null);

        Assert.Equal(HttpStatusCode.Forbidden, unassignResponse.StatusCode);
    }

    [Fact]
    public async Task Unassign_OnOpenWorkOrder_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Nothing-To-Unassign-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var unassignResponse = await org1AdminClient.PostAsync($"/api/workorders/{created!.Id}/unassign", null);

        Assert.Equal(HttpStatusCode.BadRequest, unassignResponse.StatusCode);
    }

    [Fact]
    public async Task Unassign_OnCompletedWorkOrder_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Already-Done-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/start", null);
        await org1MemberClient.PostAsync($"/api/workorders/{assigned.Id}/complete", null);

        var unassignResponse = await org1AdminClient.PostAsync($"/api/workorders/{assigned.Id}/unassign", null);

        Assert.Equal(HttpStatusCode.BadRequest, unassignResponse.StatusCode);
    }

    private static async Task<WorkOrderDto> CompleteFullLifecycleAsync(HttpClient adminClient, HttpClient assigneeClient, string title, int assigneeEmployeeId)
    {
        var createResponse = await adminClient.PostAsJsonAsync("/api/workorders", new { Title = title });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();
        await adminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = assigneeEmployeeId });
        await assigneeClient.PostAsync($"/api/workorders/{created.Id}/start", null);
        var completeResponse = await assigneeClient.PostAsync($"/api/workorders/{created.Id}/complete", null);
        return (await completeResponse.Content.ReadFromJsonAsync<WorkOrderDto>())!;
    }

    // Day 62: audit continuation — Reopen takes no body, so nothing else
    // could produce a 400 here besides ValidateMembership.
    [Fact]
    public async Task Reopen_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/workorders/999999/reopen", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reopen_ByAdmin_ReturnsToInProgress()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleAsync(org1AdminClient, org1MemberClient, $"Reopen-Me-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var reopenResponse = await org1AdminClient.PostAsync($"/api/workorders/{completed.Id}/reopen", null);
        var reopened = await reopenResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, reopenResponse.StatusCode);
        Assert.Equal(2, reopened!.Status); // WorkOrderStatus.InProgress == 2
    }

    [Fact]
    public async Task Reopen_ByMember_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleAsync(org1AdminClient, org1MemberClient, $"Member-Cannot-Reopen-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        // Even the assignee — who completed it themselves — cannot reopen;
        // this is deliberately Admin-only, unlike Reassign/Unassign.
        var reopenResponse = await org1MemberClient.PostAsync($"/api/workorders/{completed.Id}/reopen", null);

        Assert.Equal(HttpStatusCode.Forbidden, reopenResponse.StatusCode);
    }

    [Fact]
    public async Task Reopen_OnNonCompletedWorkOrder_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Not-Done-Yet-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var reopenResponse = await org1AdminClient.PostAsync($"/api/workorders/{created!.Id}/reopen", null);

        Assert.Equal(HttpStatusCode.BadRequest, reopenResponse.StatusCode);
    }

    // The exact exploit live-proven before this fix existed: an Org 2 Admin
    // reopening Org 1's completed work order just by naming its id, because
    // the first version of Reopen's authorization check never verified the
    // work order's own organization at all.
    [Fact]
    public async Task Reopen_ByAdminFromAnotherOrganization_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleAsync(org1AdminClient, org1MemberClient, $"Org1-Private-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org2AdminClient = _factory.CreateClient();
        await org2AdminClient.AuthenticateAsAsync(3);

        var reopenResponse = await org2AdminClient.PostAsync($"/api/workorders/{completed.Id}/reopen", null);

        Assert.Equal(HttpStatusCode.BadRequest, reopenResponse.StatusCode);
    }

    // Day 62: audit continuation, closing out the sweep — a valid body is
    // sent so a 400 can only mean ValidateMembership fired.
    [Fact]
    public async Task AddEvidence_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workorders/999999/evidence", new { Note = "irrelevant" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddEvidence_ByAssignee_Succeeds()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Needs-Evidence-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);

        var evidenceResponse = await org1MemberClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/evidence", new { Note = "Replaced the filter, photo attached (simulated)." });
        var updated = await evidenceResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, evidenceResponse.StatusCode);
        Assert.Contains("Replaced the filter, photo attached (simulated).", updated!.EvidenceNotes);
    }

    [Fact]
    public async Task AddEvidence_ByAdminWhoIsNotTheAssignee_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Admin-Cannot-Evidence-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var evidenceResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/evidence", new { Note = "Trying to add this as the Admin." });

        Assert.Equal(HttpStatusCode.Forbidden, evidenceResponse.StatusCode);
    }

    [Fact]
    public async Task AddEvidence_OnUnassignedWorkOrder_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Nothing-Happening-Yet-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        // Nobody is assigned yet — the Admin who created it isn't "the
        // assignee" of a null assignment, so this fails ownership (403)
        // before the Open-state rule is ever reached, same class of
        // ordering as Day 42's Start_OnUnassignedWorkOrder_ReturnsForbidden.
        var evidenceResponse = await org1AdminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/evidence", new { Note = "Should never be added." });

        Assert.Equal(HttpStatusCode.Forbidden, evidenceResponse.StatusCode);
    }

    // Day 63: applies Day 61/62's own lesson from day one — this brand new
    // action gets its own ValidateMembership test immediately, not
    // retroactively after an audit finds it missing.
    [Fact]
    public async Task GetSummary_WithoutToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/workorders/999999/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSummary_WithEvidenceNotes_ReturnsFakeAiProviderSummary()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var assigned = await CreateAndAssignWorkOrderAsync(org1AdminClient, $"Needs-Summary-{Guid.NewGuid():N}", assigneeEmployeeId: 2);

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var noteText = $"Checked the pump-{Guid.NewGuid():N}";
        await org1MemberClient.PostAsJsonAsync($"/api/workorders/{assigned.Id}/evidence", new { Note = noteText });

        var summaryResponse = await org1AdminClient.GetAsync($"/api/workorders/{assigned.Id}/summary");
        var summary = await summaryResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        Assert.Contains(noteText, summary);
        Assert.Contains("[Fake AI summary]", summary); // proves FakeAiProvider, not a real provider, answered this
    }

    [Fact]
    public async Task GetSummary_WithNoEvidenceNotes_ReturnsPlaceholder()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"No-Notes-Yet-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        var summaryResponse = await org1AdminClient.GetAsync($"/api/workorders/{created!.Id}/summary");
        var summary = await summaryResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        Assert.Contains("No evidence notes", summary);
    }

    // Day 64: failure-scenario coverage for the new IAiProvider seam —
    // FakeAiProvider never fails, so without a dedicated test like this, a
    // real provider's genuine failure modes (timeout, network error, rate
    // limit) would stay completely unexercised until one actually exists.
    // WithWebHostBuilder builds a separate, one-off host that reuses the
    // shared factory's Testcontainers database but swaps in a provider that
    // always throws, without touching the shared _factory used by every
    // other test in this class.
    //
    // A real, live-caught mistake while writing this test: the first version
    // never added an evidence note to the work order before calling
    // /summary. WorkOrderNoteSummaryService's own "no notes" short-circuit
    // (Day 63) returns its placeholder WITHOUT ever calling IAiProvider — so
    // the test passed for the wrong reason regardless of which provider was
    // registered, exactly the same class of false-positive risk Day 62
    // already caught once for an empty request body. An evidence note is
    // added first so the call genuinely reaches (and fails through)
    // ThrowingAiProvider.
    [Fact]
    public async Task GetSummary_WhenAiProviderFails_ReturnsServiceUnavailable()
    {
        var brokenAiClient = _factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IAiProvider, ThrowingAiProvider>()))
            .CreateClient();
        await brokenAiClient.AuthenticateAsAsync(1);

        var createResponse = await brokenAiClient.PostAsJsonAsync("/api/workorders", new { Title = $"AI-Failure-{Guid.NewGuid():N}" });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();
        await brokenAiClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 1 }); // self-assign, so the same client can add evidence
        await brokenAiClient.PostAsJsonAsync($"/api/workorders/{created.Id}/evidence", new { Note = "Checked the pump." });

        var summaryResponse = await brokenAiClient.GetAsync($"/api/workorders/{created.Id}/summary");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, summaryResponse.StatusCode);
    }

    // Registered only for the test above, overriding the real FakeAiProvider
    // registration — simulates a real AI provider genuinely failing (a
    // timeout, a network error), something FakeAiProvider itself can never
    // do since it's deterministic and offline by design.
    private class ThrowingAiProvider : IAiProvider
    {
        public Task<string> SummarizeAsync(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated AI provider outage.");
    }

    // Day 47: a full lifecycle, this time linked to a customer (seeded
    // Org1 customer, id=1) from creation, taken all the way to Completed —
    // the only state Approve accepts.
    private async Task<WorkOrderDto> CompleteFullLifecycleWithCustomerAsync(HttpClient adminClient, HttpClient assigneeClient, string title, int assigneeEmployeeId, int? customerId)
    {
        var createResponse = await adminClient.PostAsJsonAsync("/api/workorders", new { Title = title, CustomerId = customerId });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();
        await adminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = assigneeEmployeeId });
        await assigneeClient.PostAsync($"/api/workorders/{created.Id}/start", null);
        var completeResponse = await assigneeClient.PostAsync($"/api/workorders/{created.Id}/complete", null);
        return (await completeResponse.Content.ReadFromJsonAsync<WorkOrderDto>())!;
    }

    [Fact]
    public async Task Approve_ByLinkedCustomer_Succeeds()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleWithCustomerAsync(org1AdminClient, org1MemberClient, $"For-Customer-{Guid.NewGuid():N}", assigneeEmployeeId: 2, customerId: 1);

        var org1CustomerClient = _factory.CreateClient();
        org1CustomerClient.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        org1CustomerClient.DefaultRequestHeaders.Add("X-Customer-Id", "1"); // seeded Org1 customer, the linked one

        var approveResponse = await org1CustomerClient.PostAsync($"/api/workorders/{completed.Id}/approve", null);
        var approved = await approveResponse.Content.ReadFromJsonAsync<WorkOrderDto>();

        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        Assert.True(approved!.CustomerApproved);
    }

    // Day 61: coverage audit found Approve's explicit header-missing checks
    // (X-Organization-Id, X-Customer-Id) had code but no test — unlike every
    // other action's membership/identity checks in this file.
    [Fact]
    public async Task Approve_NoCustomerHeader_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleWithCustomerAsync(org1AdminClient, org1MemberClient, $"No-Customer-Header-{Guid.NewGuid():N}", assigneeEmployeeId: 2, customerId: 1);

        var noHeaderClient = _factory.CreateClient();
        noHeaderClient.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        // X-Customer-Id deliberately omitted.

        var approveResponse = await noHeaderClient.PostAsync($"/api/workorders/{completed.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, approveResponse.StatusCode);
    }

    [Fact]
    public async Task Approve_ByAnotherOrganizationsCustomer_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleWithCustomerAsync(org1AdminClient, org1MemberClient, $"Org1-Customer-Only-{Guid.NewGuid():N}", assigneeEmployeeId: 2, customerId: 1);

        // Org 2's own customer (id=2), claiming Org 1 in the header — fails
        // the customer/organization match before ever reaching the
        // "is this the linked customer" check.
        var wrongOrgCustomerClient = _factory.CreateClient();
        wrongOrgCustomerClient.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        wrongOrgCustomerClient.DefaultRequestHeaders.Add("X-Customer-Id", "2");

        var approveResponse = await wrongOrgCustomerClient.PostAsync($"/api/workorders/{completed.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, approveResponse.StatusCode);
    }

    [Fact]
    public async Task Approve_OnWorkOrderWithNoLinkedCustomer_ReturnsForbidden()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);
        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        var completed = await CompleteFullLifecycleWithCustomerAsync(org1AdminClient, org1MemberClient, $"No-Customer-Linked-{Guid.NewGuid():N}", assigneeEmployeeId: 2, customerId: null);

        var org1CustomerClient = _factory.CreateClient();
        org1CustomerClient.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        org1CustomerClient.DefaultRequestHeaders.Add("X-Customer-Id", "1");

        var approveResponse = await org1CustomerClient.PostAsync($"/api/workorders/{completed.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, approveResponse.StatusCode);
    }

    // Deliberately links the RIGHT customer (id=1) but stops at InProgress,
    // never Completed — isolates the state-check specifically, the same
    // discipline as Day 41's Assign_WorkOrderFromAnotherOrganization test
    // (use the correct actor so only the check actually under test can fail).
    [Fact]
    public async Task Approve_BeforeCompleted_ReturnsBadRequest()
    {
        var org1AdminClient = _factory.CreateClient();
        await org1AdminClient.AuthenticateAsAsync(1);

        var createResponse = await org1AdminClient.PostAsJsonAsync("/api/workorders", new { Title = $"Not-Completed-Yet-{Guid.NewGuid():N}", CustomerId = 1 });
        var created = await createResponse.Content.ReadFromJsonAsync<WorkOrderDto>();
        await org1AdminClient.PostAsJsonAsync($"/api/workorders/{created!.Id}/assign", new { EmployeeId = 2 });

        var org1MemberClient = _factory.CreateClient();
        await org1MemberClient.AuthenticateAsAsync(2);
        await org1MemberClient.PostAsync($"/api/workorders/{created.Id}/start", null); // now InProgress, not Completed

        var org1CustomerClient = _factory.CreateClient();
        org1CustomerClient.DefaultRequestHeaders.Add("X-Organization-Id", "1");
        org1CustomerClient.DefaultRequestHeaders.Add("X-Customer-Id", "1"); // the correct, linked customer

        var approveResponse = await org1CustomerClient.PostAsync($"/api/workorders/{created.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadRequest, approveResponse.StatusCode);
    }

    private record EmployeeDto(int Id, string Name, int OrganizationId, int Role);

    private record WorkOrderDto(int Id, string Title, int OrganizationId, int Status, int? AssignedEmployeeId, IReadOnlyList<string> EvidenceNotes, int? CustomerId, bool CustomerApproved);
}
