using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FieldOps.Api.Tests;

// Day 112: collects every log line an app writes, so a test can assert on
// what was (or wasn't) logged. ConcurrentQueue because background services
// log from other threads. Day 123: moved here from WorkOrderReportCacheWarmerTests
// so the security-event tests can reuse it.
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, string Message)> _entries = new();

    public IReadOnlyCollection<(string Category, string Message)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<(string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue((category, formatter(state, exception)));
        }
    }
}
