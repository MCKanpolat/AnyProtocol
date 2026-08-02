namespace AnyProtocol.LoadTests;

public sealed record ScenarioRegression(
    ScenarioResult Baseline,
    ScenarioResult Candidate,
    bool ThroughputRegression,
    bool P95Regression,
    bool CorrectnessFailure);

public sealed record ComparisonReport(
    IReadOnlyList<ScenarioRegression> Regressions,
    IReadOnlyList<ScenarioResult> NewScenarios);

public static class BaselineComparer
{
    public static ComparisonReport Compare(
        PerformanceRunResult baseline,
        PerformanceRunResult candidate,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.SchemaVersion != candidate.SchemaVersion ||
            baseline.SchemaVersion != PerformanceRunResult.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Performance result schema versions do not match.");
        }

        if (threshold is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        var baselineByKey = baseline.Scenarios.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var regressions = new List<ScenarioRegression>();
        var newScenarios = new List<ScenarioResult>();
        foreach (var current in candidate.Scenarios)
        {
            if (!baselineByKey.TryGetValue(Key(current), out var previous))
            {
                newScenarios.Add(current);
                continue;
            }

            var throughputRegression =
                current.OperationsPerSecond < previous.OperationsPerSecond * (1 - threshold);
            var p95Regression = current.P95Milliseconds > previous.P95Milliseconds * (1 + threshold);
            var correctnessFailure = current.Errors != 0 || current.Timeouts != 0;
            if (throughputRegression || p95Regression || correctnessFailure)
            {
                regressions.Add(new ScenarioRegression(
                    previous,
                    current,
                    throughputRegression,
                    p95Regression,
                    correctnessFailure));
            }
        }

        return new ComparisonReport(regressions, newScenarios);
    }

    private static string Key(ScenarioResult item)
        => $"{item.Transport}\u001f{item.Scenario}\u001f{item.PayloadBytes}\u001f{item.Concurrency}";
}
