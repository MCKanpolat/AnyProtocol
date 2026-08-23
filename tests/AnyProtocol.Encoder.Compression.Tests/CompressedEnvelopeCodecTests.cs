using System.Buffers.Binary;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Encoder.Compression;

namespace AnyProtocol.Encoder.Compression.Tests;

public sealed class CompressedEnvelopeCodecTests
{
    private const int FlagsOffset = 5;
    private const int AlgorithmOffset = 6;
    private const int ReservedOffset = 7;
    private const int OriginalLengthOffset = 8;
    private const int PayloadLengthOffset = 16;
    private const int PayloadOffset = 24;

    [Theory]
    [InlineData(CompressionAlgorithm.GZip)]
    [InlineData(CompressionAlgorithm.Brotli)]
    public void Round_trip_delegates_to_the_inner_codec_for_each_algorithm(
        CompressionAlgorithm algorithm)
    {
        var inner = new FakeEnvelopeCodec();
        var codec = new CompressedEnvelopeCodec(
            inner,
            new CompressedEnvelopeCodecOptions
            {
                Algorithm = algorithm,
                CompressionThreshold = 0,
                MaximumCompressionRatio = 100_000
            });
        var envelope = CreateEnvelope(Enumerable.Repeat((byte)0x2A, 32_000).ToArray());

        var frame = codec.Encode(envelope);
        var decoded = codec.Decode(frame);

        Assert.Equal((byte)algorithm, frame.Span[AlgorithmOffset]);
        Assert.Equal(0, frame.Span[FlagsOffset] & 1);
        Assert.Equal(envelope.Body.ToArray(), decoded.Body.ToArray());
        Assert.Equal(1, inner.EncodeCount);
        Assert.Equal(1, inner.DecodeCount);
    }

    [Fact]
    public void Small_frames_use_a_deterministic_explicit_uncompressed_wrapper()
    {
        var inner = new FakeEnvelopeCodec();
        var codec = new CompressedEnvelopeCodec(
            inner,
            new CompressedEnvelopeCodecOptions { CompressionThreshold = int.MaxValue });
        var envelope = CreateEnvelope([1, 2, 3, 4]);

        var frame = codec.Encode(envelope);

        Assert.Equal(1, frame.Span[FlagsOffset] & 1);
        Assert.Equal((ulong)inner.GetEncodedLength(envelope), ReadUInt64(frame, OriginalLengthOffset));
        Assert.Equal(ReadUInt64(frame, OriginalLengthOffset), ReadUInt64(frame, PayloadLengthOffset));
        Assert.Equal(envelope.Body.ToArray(), codec.Decode(frame).Body.ToArray());
    }

    [Fact]
    public void Empty_frames_use_the_uncompressed_wrapper_path()
    {
        var codec = new CompressedEnvelopeCodec(new FakeEnvelopeCodec());

        var frame = codec.Encode(CreateEnvelope([]));

        Assert.Equal(1, frame.Span[FlagsOffset] & 1);
        Assert.Equal(0UL, ReadUInt64(frame, OriginalLengthOffset) - 8);
        Assert.Equal(frame.Length - PayloadOffset, (int)ReadUInt64(frame, PayloadLengthOffset));
        Assert.Empty(codec.Decode(frame).Body.ToArray());
    }

    [Fact]
    public void The_source_envelope_is_not_mutated()
    {
        var headers = new TestHeaders(
            [
                new KeyValuePair<string, string>("X-Message", "original"),
                new KeyValuePair<string, string>("X-Count", "2")
            ]);
        var body = Enumerable.Repeat((byte)0x33, 4096).ToArray();
        var envelope = new TransportEnvelope(headers, body);
        var originalHeaders = headers.ToArray();
        var originalBody = body.ToArray();
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { CompressionThreshold = 0 });

        _ = codec.Encode(envelope);

        Assert.Equal(originalHeaders, headers.ToArray());
        Assert.Equal(originalBody, body);
        Assert.Equal("original", envelope.Headers["x-message"]);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.GZip)]
    [InlineData(CompressionAlgorithm.Brotli)]
    public void Corrupt_or_truncated_compressed_payloads_are_rejected(
        CompressionAlgorithm algorithm)
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Algorithm = algorithm,
                CompressionThreshold = 0
            });
        var encoded = codec.Encode(CreateEnvelope(Enumerable.Repeat((byte)7, 8192).ToArray())).ToArray();
        var truncated = encoded[..^1];
        var corrupt = encoded.ToArray();
        corrupt[PayloadOffset] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => codec.Decode(truncated));
        Assert.Throws<InvalidDataException>(() => codec.Decode(corrupt));
    }

    [Fact]
    public void Invalid_magic_version_algorithm_flags_reserved_bytes_and_trailing_data_are_rejected()
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { CompressionThreshold = 0 });
        var encoded = codec.Encode(CreateEnvelope(Enumerable.Repeat((byte)9, 4096).ToArray())).ToArray();

        var invalidMagic = encoded.ToArray();
        invalidMagic[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidMagic));

        var invalidVersion = encoded.ToArray();
        invalidVersion[4] = 2;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidVersion));

        var invalidAlgorithm = encoded.ToArray();
        invalidAlgorithm[AlgorithmOffset] = 0;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidAlgorithm));

        var invalidFlags = encoded.ToArray();
        invalidFlags[FlagsOffset] = 2;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidFlags));

        var invalidReserved = encoded.ToArray();
        invalidReserved[ReservedOffset] = 1;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidReserved));

        var trailingData = encoded.Concat(new byte[] { 0xA5 }).ToArray();
        Assert.Throws<InvalidDataException>(() => codec.Decode(trailingData));
    }

    [Fact]
    public void Invalid_uncompressed_length_and_payload_lengths_are_rejected()
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { CompressionThreshold = int.MaxValue });
        var encoded = codec.Encode(CreateEnvelope([1, 2, 3])).ToArray();

        var invalidOriginalLength = encoded.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            invalidOriginalLength.AsSpan(OriginalLengthOffset),
            ReadUInt64(encoded, OriginalLengthOffset) + 1);

        var invalidPayloadLength = encoded.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            invalidPayloadLength.AsSpan(PayloadLengthOffset),
            ReadUInt64(encoded, PayloadLengthOffset) + 1);

        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidOriginalLength));
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidPayloadLength));
    }

    [Fact]
    public void Decode_rejects_a_complete_frame_above_the_frame_limit()
    {
        var encoded = new CompressedEnvelopeCodec(
                new FakeEnvelopeCodec(),
                new CompressedEnvelopeCodecOptions { CompressionThreshold = int.MaxValue })
            .Encode(CreateEnvelope([1, 2, 3, 4]));
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Limits = new EnvelopeCodecLimits
                {
                    MaxFrameSize = encoded.Length - 1,
                    MaxBodySize = 4,
                    MaxDecompressedSize = 32
                }
            });

        Assert.Throws<InvalidDataException>(() => codec.Decode(encoded));
    }

    [Fact]
    public void Gzip_rejects_a_declared_decompressed_length_mismatch()
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                CompressionThreshold = 0,
                MaximumCompressionRatio = 100_000
            });
        var encoded = codec.Encode(
                CreateEnvelope(Enumerable.Repeat((byte)7, 8192).ToArray()))
            .ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            encoded.AsSpan(OriginalLengthOffset),
            ReadUInt64(encoded, OriginalLengthOffset) + 1);

        Assert.Throws<InvalidDataException>(() => codec.Decode(encoded));
    }

    [Theory]
    [InlineData(CompressionAlgorithm.GZip)]
    [InlineData(CompressionAlgorithm.Brotli)]
    public void Trailing_bytes_inside_the_declared_compressed_payload_are_rejected(
        CompressionAlgorithm algorithm)
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Algorithm = algorithm,
                CompressionThreshold = 0,
                MaximumCompressionRatio = 100_000
            });
        var encoded = codec.Encode(CreateEnvelope(Enumerable.Repeat((byte)7, 8192).ToArray())).ToArray();
        var tampered = encoded.Concat(new byte[] { 0xAA, 0xBB, 0xCC }).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            tampered.AsSpan(PayloadLengthOffset),
            (ulong)(tampered.Length - PayloadOffset));

        Assert.Throws<InvalidDataException>(() => codec.Decode(tampered));
    }

    [Fact]
    public void Gzip_rejects_a_trailing_suffix_that_repeats_the_valid_trailer()
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Algorithm = CompressionAlgorithm.GZip,
                CompressionThreshold = 0,
                MaximumCompressionRatio = 100_000
            });
        var encoded = codec.Encode(CreateEnvelope(Enumerable.Repeat((byte)7, 8192).ToArray())).ToArray();
        var gzipTrailer = encoded[^8..];
        var tampered = encoded.Concat(new byte[] { 0xAA }).Concat(gzipTrailer).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            tampered.AsSpan(PayloadLengthOffset),
            (ulong)(tampered.Length - PayloadOffset));

        Assert.Throws<InvalidDataException>(() => codec.Decode(tampered));
    }

    [Fact]
    public void Decompression_size_and_ratio_limits_are_enforced_before_delegation()
    {
        var envelope = CreateEnvelope(Enumerable.Repeat((byte)0, 32_000).ToArray());
        var encoded = new CompressedEnvelopeCodec(
                new FakeEnvelopeCodec(),
                new CompressedEnvelopeCodecOptions
                {
                    CompressionThreshold = 0,
                    MaximumCompressionRatio = 100_000
                })
            .Encode(envelope);

        var sizeLimitedCodec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Limits = new EnvelopeCodecLimits
                {
                    MaxFrameSize = 100_000,
                    MaxBodySize = 100_000,
                    MaxDecompressedSize = 100
                }
            });
        var ratioLimitedCodec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                MaximumCompressionRatio = 2
            });

        Assert.Throws<InvalidDataException>(() => sizeLimitedCodec.Decode(encoded));
        Assert.Throws<InvalidDataException>(() => ratioLimitedCodec.Decode(encoded));
    }

    [Fact]
    public void Common_body_and_complete_frame_limits_are_used_on_encode()
    {
        var bodyLimited = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Limits = new EnvelopeCodecLimits
                {
                    MaxFrameSize = 1024,
                    MaxBodySize = 2,
                    MaxDecompressedSize = 1024
                }
            });
        var frameLimited = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                CompressionThreshold = int.MaxValue,
                Limits = new EnvelopeCodecLimits
                {
                    MaxFrameSize = 50,
                    MaxBodySize = 40,
                    MaxDecompressedSize = 40
                }
            });

        Assert.Throws<InvalidDataException>(() => bodyLimited.Encode(CreateEnvelope([1, 2, 3])));
        Assert.Throws<InvalidDataException>(() => frameLimited.Encode(CreateEnvelope(Enumerable.Repeat((byte)1, 32).ToArray())));
    }

    [Fact]
    public void Encode_rejects_an_inner_frame_above_the_decompressed_size_limit()
    {
        var codec = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Limits = new EnvelopeCodecLimits
                {
                    MaxFrameSize = 1024,
                    MaxBodySize = 1024,
                    MaxDecompressedSize = 8
                }
            });

        Assert.Throws<InvalidDataException>(
            () => codec.Encode(CreateEnvelope(new byte[32])));
    }

    [Fact]
    public void Double_compression_is_rejected_unless_explicitly_enabled()
    {
        var firstLayer = new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { CompressionThreshold = 0 });
        var envelope = CreateEnvelope(Enumerable.Repeat((byte)4, 4096).ToArray());
        var outerLayer = new CompressedEnvelopeCodec(firstLayer);

        Assert.Throws<InvalidOperationException>(() => outerLayer.Encode(envelope));

        var explicitlyEnabled = new CompressedEnvelopeCodec(
            firstLayer,
            new CompressedEnvelopeCodecOptions
            {
                CompressionThreshold = 0,
                AllowDoubleCompression = true
            });
        var decoded = explicitlyEnabled.Decode(explicitlyEnabled.Encode(envelope));

        Assert.Equal(envelope.Body.ToArray(), decoded.Body.ToArray());
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() => new CompressedEnvelopeCodec(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { Algorithm = (CompressionAlgorithm)0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { CompressionThreshold = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { MaximumCompressionRatio = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { MaximumCompressionRatio = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions { MaximumCompressionRatio = double.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressedEnvelopeCodec(
            new FakeEnvelopeCodec(),
            new CompressedEnvelopeCodecOptions
            {
                Limits = new EnvelopeCodecLimits { MaxFrameSize = 0 }
            }));
    }

    [Fact]
    public void Encode_rejects_a_null_envelope()
    {
        var codec = new CompressedEnvelopeCodec(new FakeEnvelopeCodec());

        Assert.Throws<ArgumentNullException>(() => codec.Encode(null!));
    }

    private static TransportEnvelope CreateEnvelope(byte[] body)
        => new(
            new TestHeaders(
                [new KeyValuePair<string, string>("X-Test", "compression")]),
            body);

    private static ulong ReadUInt64(ReadOnlyMemory<byte> frame, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(frame.Span.Slice(offset, sizeof(ulong)));

    private sealed class FakeEnvelopeCodec : IEnvelopeCodec
    {
        private const uint Magic = 0x46414B45;
        private const int HeaderSize = 8;

        public int EncodeCount { get; private set; }

        public int DecodeCount { get; private set; }

        public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
        {
            EncodeCount++;
            var frame = new byte[HeaderSize + envelope.Body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(sizeof(uint)), envelope.Body.Length);
            envelope.Body.Span.CopyTo(frame.AsSpan(HeaderSize));
            return frame;
        }

        public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
        {
            DecodeCount++;
            if (frame.Length < HeaderSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(frame.Span) != Magic)
            {
                throw new InvalidDataException("The fake inner frame is invalid.");
            }

            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(
                frame.Span.Slice(sizeof(uint), sizeof(int)));
            if (bodyLength < 0 || bodyLength != frame.Length - HeaderSize)
            {
                throw new InvalidDataException("The fake inner frame body length is invalid.");
            }

            return new TransportEnvelope(
                new TestHeaders(),
                frame.Slice(HeaderSize, bodyLength).ToArray());
        }

        public int GetEncodedLength(TransportEnvelope envelope)
            => HeaderSize + envelope.Body.Length;
    }

    private sealed class TestHeaders : IMessageHeaders
    {
        private readonly Dictionary<string, string> _values;

        public TestHeaders(IEnumerable<KeyValuePair<string, string>>? values = null)
        {
            _values = new Dictionary<string, string>(
                values ?? [],
                StringComparer.OrdinalIgnoreCase);
        }

        public string? this[string key]
        {
            get => _values.GetValueOrDefault(key);
            set
            {
                if (value is null)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }
            }
        }

        string IReadOnlyDictionary<string, string>.this[string key] => _values[key];

        public IEnumerable<string> Keys => _values.Keys;

        public IEnumerable<string> Values => _values.Values;

        public int Count => _values.Count;

        public void Set(string key, string value) => _values[key] = value;

        public bool Remove(string key) => _values.Remove(key);

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
