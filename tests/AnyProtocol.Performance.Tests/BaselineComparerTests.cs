using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

public sealed class BaselineComparerTests
{
    [Theory]
    [InlineData(1000, 800, false)]
    [InlineData(1000, 799, true)]
    public void Throughput_regresses_only_beyond_twenty_percent(
        double baseline,
        double candidate,
        bool expectedRegression)
    {
        var report = BaselineComparer.Compare(
            PerformanceTestData.Run(PerformanceTestData.Scenario(throughput: baseline)),
            PerformanceTestData.Run(PerformanceTestData.Scenario(throughput: candidate)),
            0.20);

        Assert.Equal(expectedRegression, report.Regressions.Count != 0);
    }

    [Theory]
    [InlineData(10, 12, false)]
    [InlineData(10, 12.001, true)]
    public void P95_regresses_only_beyond_threshold(double baseline, double candidate, bool expected)
    {
        var report = BaselineComparer.Compare(
            PerformanceTestData.Run(PerformanceTestData.Scenario(p95: baseline)),
            PerformanceTestData.Run(PerformanceTestData.Scenario(p95: candidate)),
            0.20);
        Assert.Equal(expected, report.Regressions.Count != 0);
    }

    [Fact]
    public void New_scenarios_and_correctness_failures_are_classified()
    {
        var baseline = PerformanceTestData.Run(PerformanceTestData.Scenario());
        var candidate = PerformanceTestData.Run(
            PerformanceTestData.Scenario(errors: 1),
            PerformanceTestData.Scenario("rabbitmq"));

        var report = BaselineComparer.Compare(baseline, candidate, 0.20);

        Assert.Single(report.Regressions);
        Assert.Single(report.NewScenarios);
    }

    [Fact]
    public void Schema_mismatch_is_rejected()
    {
        var baseline = PerformanceTestData.Run();
        var candidate = PerformanceTestData.Run() with { SchemaVersion = 2 };
        Assert.Throws<InvalidDataException>(() => BaselineComparer.Compare(baseline, candidate, 0.20));
    }
}
