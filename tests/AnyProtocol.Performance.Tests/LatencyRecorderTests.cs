using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

public sealed class LatencyRecorderTests
{
    [Fact]
    public void Complete_reports_nearest_rank_percentiles_and_throughput()
    {
        var recorder = new LatencyRecorder();
        foreach (var milliseconds in Enumerable.Range(1, 100))
        {
            recorder.Record(TimeSpan.FromMilliseconds(milliseconds));
        }

        var result = recorder.Complete(100, TimeSpan.FromSeconds(2));

        Assert.Equal(50, result.P50Milliseconds);
        Assert.Equal(95, result.P95Milliseconds);
        Assert.Equal(99, result.P99Milliseconds);
        Assert.Equal(50, result.OperationsPerSecond);
    }

    [Fact]
    public void Complete_rejects_empty_or_mismatched_samples()
    {
        var empty = new LatencyRecorder();
        Assert.Throws<InvalidOperationException>(() => empty.Complete(0, TimeSpan.FromSeconds(1)));

        var recorder = new LatencyRecorder();
        recorder.Record(TimeSpan.FromMilliseconds(1.23456));
        Assert.Throws<InvalidOperationException>(() => recorder.Complete(2, TimeSpan.FromSeconds(1)));
        Assert.Equal(1.235, recorder.Complete(1, TimeSpan.FromSeconds(1)).P50Milliseconds);
    }
}
