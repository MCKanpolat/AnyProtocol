using BenchmarkDotNet.Attributes;
using AnyProtocol.Abstraction;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly BinaryEnvelopeCodec _codec = new();
    private TransportEnvelope _envelope = null!;
    private ReadOnlyMemory<byte> _frame;

    [Params(256, 4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _envelope = new TransportEnvelope(
            new MessageHeaders
            {
                [HeaderNames.MessageId] = "benchmark-message",
                [HeaderNames.ContentType] = "application/octet-stream"
            },
            BenchmarkPayload.Create(PayloadBytes).Data);
        _frame = _codec.Encode(_envelope);
    }

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> Encode() => _codec.Encode(_envelope);

    [Benchmark]
    public TransportEnvelope Decode() => _codec.Decode(_frame);
}
