namespace AnyProtocol.LoadTests;

public sealed class LatencyRecorder
{
    private readonly object _gate = new();
    private readonly List<long> _ticks = [];

    public void Record(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        lock (_gate)
        {
            _ticks.Add(elapsed.Ticks);
        }
    }

    public LatencyStatistics Complete(int completedOperations, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        long[] samples;
        lock (_gate)
        {
            if (_ticks.Count == 0)
            {
                throw new InvalidOperationException("At least one latency sample is required.");
            }

            if (_ticks.Count != completedOperations)
            {
                throw new InvalidOperationException(
                    "The completed operation count must match the latency sample count.");
            }

            samples = [.. _ticks];
        }

        Array.Sort(samples);
        return new LatencyStatistics(
            Round(completedOperations / elapsed.TotalSeconds),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99));
    }

    private static double Percentile(long[] sortedTicks, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedTicks.Length);
        return Round(TimeSpan.FromTicks(sortedTicks[Math.Max(rank - 1, 0)]).TotalMilliseconds);
    }

    private static double Round(double value)
        => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
