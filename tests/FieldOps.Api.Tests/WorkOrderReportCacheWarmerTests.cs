using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FieldOps.Api.Tests;

// Day 112: found in Azure logs on Day 109. Since Day 108's fix, the report
// computes from the database when Redis is unreachable — right for a user's
// request, but the background warmer did the same every 10 seconds for every
// organization, and the result could never be cached: pure wasted database
// work. While Redis is down the warmer must skip its ticks entirely.
//
// Observed through logs: WorkOrderReportService logs "Redis read failed"
// right before computing from the database, so if that never appears while
// no HTTP request is made, the warmer never asked for a report.
public class WorkOrderReportCacheWarmerTests : IClassFixture<FieldOpsApiFactory>
{
    private readonly FieldOpsApiFactory _factory;

    public WorkOrderReportCacheWarmerTests(FieldOpsApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Warmer_RedisUnreachable_SkipsTicksInsteadOfComputingReports()
    {
        var logs = new CapturingLoggerProvider();
        using var app = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "Redis:ConnectionString",
                "localhost:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200");
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs));
        });

        // Creating a client starts the host (and its background services).
        // No request is sent: anything logged below comes from the warmer.
        _ = app.CreateClient();

        // The warmer ticks every 10 seconds — wait for two or three ticks.
        await Task.Delay(TimeSpan.FromSeconds(25));

        Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Redis read failed for work order report"));
        Assert.Single(logs.Entries, e => e.Message.StartsWith("Redis is unavailable; pausing work order report cache warming"));
    }
}
