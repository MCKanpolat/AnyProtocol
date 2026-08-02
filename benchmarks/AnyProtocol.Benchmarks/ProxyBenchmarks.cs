using BenchmarkDotNet.Attributes;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class ProxyBenchmarks
{
    private readonly Calculator _implementation = new();
    private readonly ICalculator _proxy;

    public ProxyBenchmarks() => _proxy = _implementation;

    [Benchmark(Baseline = true)]
    public int DirectCall() => _implementation.Add(20, 22);

    [Benchmark]
    public int InterfaceDispatch() => _proxy.Add(20, 22);

    public interface ICalculator
    {
        int Add(int left, int right);
    }

    private sealed class Calculator : ICalculator
    {
        public int Add(int left, int right) => left + right;
    }
}
