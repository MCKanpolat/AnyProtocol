using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

internal static class PerformanceTestData
{
    public static ScenarioResult Scenario(
        string transport = "inmemory",
        string scenario = "event",
        int payloadBytes = 256,
        int concurrency = 1,
        double throughput = 1000,
        double p95 = 1,
        int errors = 0,
        int timeouts = 0)
        => new(
            transport,
            scenario,
            payloadBytes,
            concurrency,
            100,
            throughput,
            0.5,
            p95,
            2,
            0,
            0,
            0,
            0,
            errors,
            timeouts);

    public static PerformanceRunResult Run(params ScenarioResult[] scenarios)
        => new(
            PerformanceRunResult.CurrentSchemaVersion,
            new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero),
            new RuntimeMetadata(".NET 10", "Test OS", "x64", "Test CPU", 8),
            scenarios);
}
