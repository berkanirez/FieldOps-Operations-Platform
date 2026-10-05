using System.Net;
using System.Net.Http.Json;
using FieldOps.Api.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FieldOps.Api.Tests;

// Day 123 (SECURITY_REVIEW.md F12): failed logins and rate-limit rejections
// are written as security events under one log category.
public class SecurityEventLoggingTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public SecurityEventLoggingTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FailedLogin_IsLoggedAsSecurityEvent_WithoutThePassword()
    {
        var logs = new CapturingLoggerProvider();
        var client = _factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)))
            .CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 999, Password = "Very-Secret-Guess-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(logs.Entries, e => e.Category == SecurityEvents.Category && e.Message.Contains(SecurityEvents.LoginFailed));
        Assert.Contains("999", entry.Message);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("Very-Secret-Guess-1"));
    }

    [Fact]
    public async Task RateLimitRejection_IsLoggedAsSecurityEvent()
    {
        var logs = new CapturingLoggerProvider();
        var client = _factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:Login:PermitLimit", "1");
                builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
            })
            .CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = "guess-1" });
        var second = await client.PostAsJsonAsync("/api/auth/login", new { EmployeeId = 1, Password = "guess-2" });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains(logs.Entries, e => e.Category == SecurityEvents.Category
            && e.Message.Contains(SecurityEvents.RateLimitExceeded)
            && e.Message.Contains("Login"));
    }
}
