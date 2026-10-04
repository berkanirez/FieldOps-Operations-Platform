using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace FieldOps.Api.Tests;

// Day 108: found live on Day 107 when FieldOps.Api ran on Azure without
// Redis — GET /api/workorders/report returned 500 because
// WorkOrderReportService.GetStatusReport let Redis exceptions escape. No test
// had ever called this endpoint, which is why the gap survived since Day 48.
// A cache-aside cache is an accelerator, not a dependency: when Redis is
// unreachable the report must still be computed from the database.
public class WorkOrderReportCacheResilienceTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public WorkOrderReportCacheResilienceTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetStatusReport_RedisUnreachable_StillReturnsCountsFromDatabase()
    {
        // Point Redis at an address where nothing listens, regardless of
        // whether a developer happens to have Redis running locally — the
        // test must exercise the "cache down" path deterministically. Short
        // timeouts keep it fast.
        var client = _factory
            .WithWebHostBuilder(builder => builder.UseSetting(
                "Redis:ConnectionString",
                "localhost:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200"))
            .CreateClient();

        // Organization 2's seeded Admin (employee 3). This test class gets its
        // own factory — and so its own fresh database — so the count is exact.
        client.DefaultRequestHeaders.Add("X-Organization-Id", "2");
        client.DefaultRequestHeaders.Add("X-Employee-Id", "3");

        var create = await client.PostAsJsonAsync("/api/workorders", new { Title = "Report resilience check" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var response = await client.GetAsync("/api/workorders/report");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ReportResponse>();
        Assert.NotNull(report);
        Assert.Equal(2, report.OrganizationId);
        Assert.Equal(1, report.Open);
    }

    private record ReportResponse(int OrganizationId, int Open, int Assigned, int InProgress, int Completed);
}
