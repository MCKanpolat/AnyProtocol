using BenchmarkDotNet.Attributes;
using AnyProtocol.Benchmarks;

namespace AnyProtocol.Performance.Tests;

public sealed class BenchmarkSmokeTests
{
    [Fact]
    public void Required_benchmark_classes_are_discoverable_and_parameterized()
    {
        Type[] required =
        [
            typeof(CodecBenchmarks),
            typeof(SerializerBenchmarks),
            typeof(PipelineBenchmarks),
            typeof(ProxyBenchmarks),
            typeof(InMemoryBenchmarks)
        ];

        Assert.All(required, type =>
        {
            Assert.Contains(type.GetMethods(), method => method.IsDefined(typeof(BenchmarkAttribute), inherit: true));
            Assert.Single(
                type.GetMethods(),
                method => method.GetCustomAttributes(typeof(BenchmarkAttribute), true)
                    .Cast<BenchmarkAttribute>()
                    .Any(attribute => attribute.Baseline));
        });

        AssertPayloadParameters(typeof(CodecBenchmarks));
        AssertPayloadParameters(typeof(SerializerBenchmarks));
        AssertPayloadParameters(typeof(InMemoryBenchmarks));
    }

    private static void AssertPayloadParameters(Type type)
    {
        var property = type.GetProperty("PayloadBytes");
        var values = property?.GetCustomAttributes(typeof(ParamsAttribute), true)
            .Cast<ParamsAttribute>()
            .Single()
            .Values;
        Assert.Equal([256, 4096, 65536], values);
    }
}
