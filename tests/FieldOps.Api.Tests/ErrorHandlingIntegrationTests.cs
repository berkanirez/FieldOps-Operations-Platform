using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using FieldOps.Modules.Organizations;

namespace FieldOps.Api.Tests;

// Day 123 (SECURITY_REVIEW.md F10): an unhandled exception must come back as
// an RFC 9110 ProblemDetails 500 — with a traceId to correlate with the logs,
// and without the exception message or stack trace.
public class ErrorHandlingIntegrationTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public ErrorHandlingIntegrationTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnhandledException_ReturnsProblemDetailsWithoutInternals()
    {
        var client = _factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddScoped<IOrganizationDirectory, ExplodingOrganizationDirectory>()))
            .CreateClient();
        await client.AuthenticateAsAsync(1);

        var response = await client.GetAsync("/api/organizations");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"traceId\"", body);
        Assert.DoesNotContain(ExplodingOrganizationDirectory.SecretDetail, body);
        Assert.DoesNotContain("   at ", body); // no stack trace frames
    }

    // Registered last, so it wins over the module's real implementation.
    private sealed class ExplodingOrganizationDirectory : IOrganizationDirectory
    {
        public const string SecretDetail = "connection string Server=internal-db;Password=hunter2";

        public IReadOnlyList<OrganizationSummary> GetAll() => throw new InvalidOperationException(SecretDetail);

        public OrganizationSummary? GetById(int id) => throw new InvalidOperationException(SecretDetail);
    }
}
