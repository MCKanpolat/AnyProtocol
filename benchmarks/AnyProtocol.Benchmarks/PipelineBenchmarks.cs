using BenchmarkDotNet.Attributes;
using AnyProtocol.Abstraction;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private readonly IMessageContext _context = new MessageContext(
        new MessageHeaders(),
        ReadOnlyMemory<byte>.Empty,
        "benchmarks.pipeline",
        MessageType.Event,
        MessageDirection.Outbound);
    private MessageFilterDelegate _zero = null!;
    private MessageFilterDelegate _one = null!;
    private MessageFilterDelegate _five = null!;

    [GlobalSetup]
    public void Setup()
    {
        _zero = PipelineBuilder.Build([], static _ => ValueTask.CompletedTask);
        _one = PipelineBuilder.Build([new NoOpFilter()], static _ => ValueTask.CompletedTask);
        _five = PipelineBuilder.Build(
            Enumerable.Range(0, 5).Select(static _ => (IMessageFilter)new NoOpFilter()),
            static _ => ValueTask.CompletedTask);
    }

    [Benchmark(Baseline = true)]
    public ValueTask ZeroFilters() => _zero(_context);

    [Benchmark]
    public ValueTask OneFilter() => _one(_context);

    [Benchmark]
    public ValueTask FiveFilters() => _five(_context);

    private sealed class NoOpFilter : IMessageFilter
    {
        public ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next) => next(context);
    }
}
