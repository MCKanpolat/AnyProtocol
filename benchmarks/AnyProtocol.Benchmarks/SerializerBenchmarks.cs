using BenchmarkDotNet.Attributes;
using AnyProtocol.Serializer.MessagePack;
using AnyProtocol.Serializer.TextJson;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class SerializerBenchmarks
{
    private readonly TextJsonMessageSerializer _json = new();
    private readonly MessagePackMessageSerializer _messagePack = new();
    private BenchmarkPayload _payload = null!;

    [Params(256, 4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup() => _payload = BenchmarkPayload.Create(PayloadBytes);

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> TextJson() => _json.Serialize(_payload);

    [Benchmark]
    public ReadOnlyMemory<byte> MessagePack() => _messagePack.Serialize(_payload);

    [GlobalCleanup]
    public void Cleanup()
    {
        _json.Dispose();
        _messagePack.Dispose();
    }
}
