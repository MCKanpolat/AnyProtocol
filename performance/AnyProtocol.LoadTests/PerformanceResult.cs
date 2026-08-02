namespace AnyProtocol.LoadTests;

public sealed record PerformanceRunResult(
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    RuntimeMetadata Runtime,
    IReadOnlyList<ScenarioResult> Scenarios)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record RuntimeMetadata(
    string Framework,
    string OperatingSystem,
    string Architecture,
    string Processor,
    int ProcessorCount);

public sealed record ScenarioResult(
    string Transport,
    string Scenario,
    int PayloadBytes,
    int Concurrency,
    int Operations,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int Errors,
    int Timeouts);

public readonly record struct LatencyStatistics(
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds);
