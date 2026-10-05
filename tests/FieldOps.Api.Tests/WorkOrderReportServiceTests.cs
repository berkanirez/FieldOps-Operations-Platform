using System.Reflection;
using FieldOps.Api.Application;
using FieldOps.Modules.WorkOrders;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace FieldOps.Api.Tests;

// Day 117: found by the first load test — at 50 virtual users, 11% of
// requests failed with an unhandled RedisTimeoutException from the report's
// cache read. Day 108's fix caught RedisException, assuming timeouts derive
// from it; they don't (RedisTimeoutException -> System.TimeoutException,
// verified by reflection). A timed-out cache read or write must degrade to
// the database result just like a connection failure.
//
// IConnectionMultiplexer and IDatabase have dozens of members, and no
// mocking library is used in this project, so DispatchProxy (built into
// .NET) creates minimal fakes: every call lands in one handler, which
// answers only the calls this test needs.
public class WorkOrderReportServiceTests
{
    [Fact]
    public async Task GetStatusReport_RedisTimesOut_ReturnsCountsFromDatabase()
    {
        // Day 118: the service is async now — the fake throws from the async
        // Redis calls the same RedisTimeoutException the load test produced.
        var database = Fake<IDatabase>((method, _) => method.Name is "StringGetAsync" or "StringSetAsync"
            ? throw new RedisTimeoutException(CommandFlags.None, "Timeout performing GET (500ms)", CommandStatus.Sent)
            : throw new NotSupportedException(method.Name));
        var redis = Fake<IConnectionMultiplexer>((method, _) => method.Name == "GetDatabase"
            ? database
            : throw new NotSupportedException(method.Name));
        var counts = new Dictionary<WorkOrderStatus, int> { [WorkOrderStatus.Open] = 2, [WorkOrderStatus.Completed] = 1 };
        var directory = Fake<IWorkOrderDirectory>((method, _) => method.Name == nameof(IWorkOrderDirectory.GetStatusCountsAsync)
            ? Task.FromResult<IReadOnlyDictionary<WorkOrderStatus, int>>(counts)
            : throw new NotSupportedException(method.Name));
        var service = new WorkOrderReportService(directory, redis, NullLogger<WorkOrderReportService>.Instance);

        var report = await service.GetStatusReportAsync(organizationId: 1, CancellationToken.None);

        Assert.Equal(1, report.OrganizationId);
        Assert.Equal(2, report.Open);
        Assert.Equal(0, report.Assigned);
        Assert.Equal(1, report.Completed);
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, HandlerProxy>();
        ((HandlerProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class HandlerProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
