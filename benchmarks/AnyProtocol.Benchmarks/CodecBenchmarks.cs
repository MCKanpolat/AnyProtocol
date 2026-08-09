using BenchmarkDotNet.Attributes;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Encoder.Compression;
using AnyProtocol.Encoder.MessagePack;
using AnyProtocol.Encoder.Protobuf;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly IEnvelopeCodec _binaryCodec = new BinaryEnvelopeCodec();
    private readonly IEnvelopeCodec _messagePackCodec = new MessagePackEnvelopeCodec();
    private readonly IEnvelopeCodec _protobufCodec = new ProtobufEnvelopeCodec();
    private readonly IEnvelopeCodec _compressedCodec = new CompressedEnvelopeCodec(
        new BinaryEnvelopeCodec(),
        new CompressedEnvelopeCodecOptions { CompressionThreshold = 0 });
    private TransportEnvelope _envelope = null!;
    private ReadOnlyMemory<byte> _binaryFrame;
    private ReadOnlyMemory<byte> _messagePackFrame;
    private ReadOnlyMemory<byte> _protobufFrame;
    private ReadOnlyMemory<byte> _compressedFrame;

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
        _binaryFrame = _binaryCodec.Encode(_envelope);
        _messagePackFrame = _messagePackCodec.Encode(_envelope);
        _protobufFrame = _protobufCodec.Encode(_envelope);
        _compressedFrame = _compressedCodec.Encode(_envelope);
    }

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> Encode() => _binaryCodec.Encode(_envelope);

    [Benchmark]
    public ReadOnlyMemory<byte> MessagePackEncode() => _messagePackCodec.Encode(_envelope);

    [Benchmark]
    public ReadOnlyMemory<byte> ProtobufEncode() => _protobufCodec.Encode(_envelope);

    [Benchmark]
    public ReadOnlyMemory<byte> CompressedEncode() => _compressedCodec.Encode(_envelope);

    [Benchmark]
    public TransportEnvelope Decode() => _binaryCodec.Decode(_binaryFrame);

    [Benchmark]
    public TransportEnvelope MessagePackDecode() => _messagePackCodec.Decode(_messagePackFrame);

    [Benchmark]
    public TransportEnvelope ProtobufDecode() => _protobufCodec.Decode(_protobufFrame);

    [Benchmark]
    public TransportEnvelope CompressedDecode() => _compressedCodec.Decode(_compressedFrame);

    [Benchmark]
    public int BinaryEncodedSize() => _binaryFrame.Length;

    [Benchmark]
    public int MessagePackEncodedSize() => _messagePackFrame.Length;

    [Benchmark]
    public int ProtobufEncodedSize() => _protobufFrame.Length;

    [Benchmark]
    public int CompressedEncodedSize() => _compressedFrame.Length;

    [Benchmark]
    public double CompressionRatio() => _binaryFrame.Length / (double)_compressedFrame.Length;
}
