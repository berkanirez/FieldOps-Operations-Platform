using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace FieldOps.Api.Tests;

// Day 122: SECURITY_REVIEW.md F2 (credential-less login + employee
// enumeration), F7 (unlimited login) and F5/F6 (create rate limit keyed on a
// forgeable header). Each test proves one property of the fixed behaviour.
public class AuthenticationIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public AuthenticationIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_CorrectPassword_ReturnsToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = TestAuth.DemoPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.False(string.IsNullOrEmpty(body!.Token));
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Before Day 122 an unknown id returned 404 "Employee 999 does not exist"
    // while a known one returned a token — enough to list every valid id.
    [Fact]
    public async Task Login_UnknownEmployee_LooksExactlyLikeWrongPassword()
    {
        var client = _factory.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 999, Password = "not-the-password" });
        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknown.StatusCode);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<ProblemDetails>();
        var wrongPasswordBody = await wrongPassword.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(wrongPasswordBody!.Title, unknownBody!.Title);
        Assert.Equal(wrongPasswordBody.Detail, unknownBody.Detail);
    }

    [Fact]
    public async Task Login_TooManyAttempts_ReturnsTooManyRequests()
    {
        var client = _factory
            .WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:Login:PermitLimit", "3"))
            .CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = "guess-" + attempt });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    // F5/F6: the create limit follows the organization in the token. A
    // rewritten header ("01") no longer opens a fresh bucket, and one
    // organization exhausting its quota leaves another's untouched.
    [Fact]
    public async Task CreateRateLimit_FollowsTokenOrganization_NotHeaders()
    {
        var app = _factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:PerOrganization:PermitLimit", "2"));
        var org1 = app.CreateClient();
        await org1.AuthenticateAsAsync(1);
        var org2 = app.CreateClient();
        await org2.AuthenticateAsAsync(3);

        var first = await org1.PostAsJsonAsync("/api/workorders", new { Title = "Limit 1" });
        var second = await org1.PostAsJsonAsync("/api/workorders", new { Title = "Limit 2" });
        var third = await org1.PostAsJsonAsync("/api/workorders", new { Title = "Limit 3" });
        org1.DefaultRequestHeaders.Add("X-Organization-Id", "01");
        var rewrittenHeader = await org1.PostAsJsonAsync("/api/workorders", new { Title = "Limit bypass attempt" });
        var otherOrganization = await org2.PostAsJsonAsync("/api/workorders", new { Title = "Org 2 unaffected" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rewrittenHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherOrganization.StatusCode);
    }

    private record LoginResult(string Token);
}
