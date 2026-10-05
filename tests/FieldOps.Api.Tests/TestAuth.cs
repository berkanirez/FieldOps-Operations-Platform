using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FieldOps.Api.Tests;

// Day 121: the API now requires a bearer token and takes the caller's
// identity from it (not from X-Organization-Id/X-Employee-Id headers). Tests
// get their token the way a real client does — from POST /api/auth/login —
// so every authenticated test also exercises the login endpoint and the
// real token validation, with no test-only shortcut.
public static class TestAuth
{
    public static async Task AuthenticateAsAsync(this HttpClient client, int employeeId)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = employeeId });
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResult>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
    }

    private record LoginResult(string Token);
}
