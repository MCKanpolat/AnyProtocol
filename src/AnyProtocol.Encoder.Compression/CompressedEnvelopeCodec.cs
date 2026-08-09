using System.Buffers.Binary;
using System.IO.Compression;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol.Encoder.Compression;

/// <summary>
/// Decorates an envelope codec with a versioned compression wrapper.
/// </summary>
/// <remarks>
/// The wrapper is independent of the inner envelope schema and can therefore wrap
/// binary, MessagePack, Protobuf, or another compatible envelope codec. Its wire
/// format is little-endian: four magic bytes, one version byte, one flags byte,
/// one algorithm byte, one reserved byte, an unsigned 64-bit original length, an
/// unsigned 64-bit payload length, and the bounded payload.
/// </remarks>
public sealed class CompressedEnvelopeCodec : IEnvelopeCodec
{
    private const uint Magic = 0x5A435041;
    private const byte Version = 1;
    private const byte UncompressedFlag = 1;
    private const int HeaderSize = sizeof(uint) + sizeof(byte) + sizeof(byte) +
                                   sizeof(byte) + sizeof(byte) + sizeof(ulong) +
                                   sizeof(ulong);
    private static readonly uint[] Crc32Table = CreateCrc32Table();
    private readonly IEnvelopeCodec _inner;
    private readonly CompressedEnvelopeCodecOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompressedEnvelopeCodec"/> class.
    /// </summary>
    /// <param name="inner">The envelope codec being decorated.</param>
    /// <param name="options">The compression options.</param>
    public CompressedEnvelopeCodec(
        IEnvelopeCodec inner,
        CompressedEnvelopeCodecOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _options = options ?? new CompressedEnvelopeCodecOptions();
        _options.Validate();
    }

    /// <summary>
    /// Encodes an envelope using the inner codec and then wraps the result.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <returns>The compressed or explicitly uncompressed wrapper frame.</returns>
    public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Headers);

        if (envelope.Body.Length > _options.Limits.MaxBodySize)
        {
            throw new InvalidDataException(
                $"The envelope body exceeds the maximum size of {_options.Limits.MaxBodySize} bytes.");
        }

        var innerFrame = _inner.Encode(envelope);
        if (innerFrame.Length > _options.Limits.MaxDecompressedSize)
        {
            throw new InvalidDataException(
                $"The inner envelope frame exceeds the maximum decompressed size of {_options.Limits.MaxDecompressedSize} bytes.");
        }

        if (!_options.AllowDoubleCompression && LooksLikeCompressedFrame(innerFrame.Span))
        {
            throw new InvalidOperationException(
                "The compressed envelope codec refuses to apply compression twice. Set AllowDoubleCompression to enable nested wrappers.");
        }

        var compressed = innerFrame.Length >= _options.CompressionThreshold
            ? Compress(innerFrame.Span, _options.Algorithm)
            : null;
        var useCompressedPayload = compressed is not null &&
                                   compressed.Length < innerFrame.Length &&
                                   IsWithinCompressionRatio(innerFrame.Length, compressed.Length);
        var payload = useCompressedPayload ? compressed! : innerFrame.ToArray();
        var frameLength = checked(HeaderSize + payload.Length);
        if (frameLength > _options.Limits.MaxFrameSize)
        {
            throw new InvalidDataException(
                $"The compressed frame exceeds the maximum size of {_options.Limits.MaxFrameSize} bytes.");
        }

        return CreateFrame(
            (ulong)innerFrame.Length,
            payload,
            useCompressedPayload ? (byte)0 : UncompressedFlag,
            _options.Algorithm);
    }

    /// <summary>
    /// Validates, decompresses, and delegates a wrapper frame to the inner codec.
    /// </summary>
    /// <param name="frame">The wrapper frame to process.</param>
    /// <returns>The envelope decoded by the inner codec.</returns>
    public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > _options.Limits.MaxFrameSize)
        {
            throw new InvalidDataException(
                $"The compressed frame exceeds the maximum size of {_options.Limits.MaxFrameSize} bytes.");
        }

        if (frame.Length < HeaderSize)
        {
            throw new InvalidDataException("The compressed frame is truncated before its header is complete.");
        }

        var bytes = frame.Span;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
        {
            throw new InvalidDataException("The frame is not a compressed AnyProtocol envelope.");
        }

        var version = bytes[sizeof(uint)];
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported compressed envelope version '{version}'.");
        }

        var flags = bytes[sizeof(uint) + sizeof(byte)];
        if ((flags & ~UncompressedFlag) != 0)
        {
            throw new InvalidDataException($"Invalid compressed envelope flags '{flags}'.");
        }

        var algorithmOffset = sizeof(uint) + sizeof(byte) + sizeof(byte);
        var algorithmValue = bytes[algorithmOffset];
        if (!Enum.IsDefined(typeof(CompressionAlgorithm), algorithmValue))
        {
            throw new InvalidDataException($"Invalid compression algorithm '{algorithmValue}'.");
        }

        var reservedOffset = algorithmOffset + sizeof(byte);
        if (bytes[reservedOffset] != 0)
        {
            throw new InvalidDataException("The compressed envelope contains non-zero reserved flags.");
        }

        var originalLengthOffset = reservedOffset + sizeof(byte);
        var originalLength = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.Slice(originalLengthOffset, sizeof(ulong)));
        if (originalLength > (ulong)int.MaxValue ||
            originalLength > (ulong)_options.Limits.MaxDecompressedSize)
        {
            throw new InvalidDataException(
                $"The decompressed frame exceeds the maximum size of {_options.Limits.MaxDecompressedSize} bytes.");
        }

        var payloadLengthOffset = originalLengthOffset + sizeof(ulong);
        var payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.Slice(payloadLengthOffset, sizeof(ulong)));
        var actualPayloadLength = bytes.Length - HeaderSize;
        if (payloadLength != (ulong)actualPayloadLength)
        {
            throw new InvalidDataException("The compressed envelope payload length is invalid or the frame is truncated.");
        }

        var expectedLength = (int)originalLength;
        var payload = bytes.Slice(HeaderSize, actualPayloadLength);
        var algorithm = (CompressionAlgorithm)algorithmValue;
        var isUncompressed = (flags & UncompressedFlag) != 0;
        byte[] innerFrame;

        if (isUncompressed)
        {
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "An uncompressed wrapper must contain a payload equal to its original length.");
            }

            innerFrame = payload.ToArray();
        }
        else
        {
            if (!IsWithinCompressionRatio(expectedLength, payload.Length))
            {
                throw new InvalidDataException("The compressed envelope exceeds the configured compression ratio.");
            }

            innerFrame = Decompress(payload, algorithm, expectedLength);
        }

        return _inner.Decode(innerFrame);
    }

    private bool IsWithinCompressionRatio(int originalLength, int compressedLength)
    {
        if (originalLength == 0)
        {
            return true;
        }

        return compressedLength > 0 &&
               originalLength / (double)compressedLength <= _options.MaximumCompressionRatio;
    }

    private static byte[] Compress(ReadOnlySpan<byte> input, CompressionAlgorithm algorithm)
    {
        using var destination = new MemoryStream();
        using (var compressor = CreateCompressionStream(destination, algorithm, CompressionMode.Compress))
        {
            compressor.Write(input);
        }

        return destination.ToArray();
    }

    private static byte[] Decompress(
        ReadOnlySpan<byte> payload,
        CompressionAlgorithm algorithm,
        int expectedLength)
        => algorithm switch
        {
            CompressionAlgorithm.GZip => DecompressGZip(payload, expectedLength),
            CompressionAlgorithm.Brotli => DecompressBrotli(payload, expectedLength),
            _ => throw new InvalidDataException($"Invalid compression algorithm '{algorithm}'.")
        };

    private static byte[] DecompressGZip(ReadOnlySpan<byte> payload, int expectedLength)
    {
        using var source = new MemoryStream(payload.ToArray(), writable: false);
        using var destination = new BoundedMemoryStream(expectedLength);

        try
        {
            using (var decompressor = new GZipStream(
                       source,
                       CompressionMode.Decompress,
                       leaveOpen: true))
            {
                decompressor.CopyTo(destination);
            }

            if (destination.Length != expectedLength)
            {
                throw new InvalidDataException(
                    $"The decompressed payload length '{destination.Length}' does not match the declared length '{expectedLength}'.");
            }

            var decompressed = destination.ToArray();
            ValidateSingleGZipMember(payload, decompressed);
            return decompressed;
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("The compressed payload is corrupt or invalid.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The compressed payload is corrupt or truncated.", exception);
        }
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> payload, int expectedLength)
    {
        var decompressed = new byte[expectedLength];
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(
            payload,
            decompressed,
            out var bytesConsumed,
            out var bytesWritten);
        if (status != System.Buffers.OperationStatus.Done ||
            bytesConsumed != payload.Length ||
            bytesWritten != expectedLength)
        {
            throw new InvalidDataException("The Brotli payload is corrupt, truncated, or contains trailing data.");
        }

        return decompressed;
    }

    private static void ValidateSingleGZipMember(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> decompressed)
    {
        Span<byte> expectedTrailer = stackalloc byte[sizeof(uint) * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedTrailer, ComputeCrc32(decompressed));
        BinaryPrimitives.WriteUInt32LittleEndian(
            expectedTrailer[sizeof(uint)..],
            (uint)decompressed.Length);

        var trailerIndex = payload.IndexOf(expectedTrailer);
        if (trailerIndex != payload.Length - expectedTrailer.Length)
        {
            throw new InvalidDataException("The GZip payload contains trailing data or multiple members.");
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = Crc32Table[(byte)(crc ^ value)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ (0xEDB88320U & (uint)-(int)(value & 1));
            }

            table[index] = value;
        }

        return table;
    }

    private static Stream CreateCompressionStream(
        Stream stream,
        CompressionAlgorithm algorithm,
        CompressionMode mode)
    {
        if (mode == CompressionMode.Compress)
        {
            return algorithm switch
            {
                CompressionAlgorithm.GZip => new GZipStream(
                    stream,
                    CompressionLevel.SmallestSize,
                    leaveOpen: true),
                CompressionAlgorithm.Brotli => new BrotliStream(
                    stream,
                    CompressionLevel.SmallestSize,
                    leaveOpen: true),
                _ => throw new InvalidDataException($"Invalid compression algorithm '{algorithm}'.")
            };
        }

        return algorithm switch
        {
            CompressionAlgorithm.GZip => new GZipStream(
                stream,
                CompressionMode.Decompress,
                leaveOpen: true),
            CompressionAlgorithm.Brotli => new BrotliStream(
                stream,
                CompressionMode.Decompress,
                leaveOpen: true),
            _ => throw new InvalidDataException($"Invalid compression algorithm '{algorithm}'.")
        };
    }

    private static byte[] CreateFrame(
        ulong originalLength,
        byte[] payload,
        byte flags,
        CompressionAlgorithm algorithm)
    {
        var frameLength = checked(HeaderSize + payload.Length);
        var frame = new byte[frameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        frame[sizeof(uint)] = Version;
        frame[sizeof(uint) + sizeof(byte)] = flags;
        frame[sizeof(uint) + sizeof(byte) + sizeof(byte)] = (byte)algorithm;
        BinaryPrimitives.WriteUInt64LittleEndian(
            frame.AsSpan(sizeof(uint) + sizeof(byte) + sizeof(byte) + sizeof(byte) + sizeof(byte)),
            originalLength);
        BinaryPrimitives.WriteUInt64LittleEndian(
            frame.AsSpan(HeaderSize - sizeof(ulong)),
            (ulong)payload.Length);
        payload.CopyTo(frame, HeaderSize);
        return frame;
    }

    private static bool LooksLikeCompressedFrame(ReadOnlySpan<byte> frame)
        => frame.Length >= sizeof(uint) &&
           BinaryPrimitives.ReadUInt32LittleEndian(frame) == Magic;

    private sealed class BoundedMemoryStream(int maximumLength) : Stream
    {
        private readonly MemoryStream _inner = new(maximumLength);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count < 0 || offset < 0 || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }

        public byte[] ToArray() => _inner.ToArray();

        private void EnsureCapacity(int count)
        {
            if (count > _inner.Capacity - _inner.Length)
            {
                throw new InvalidDataException("The decompressed payload exceeds its declared length.");
            }
        }
    }
}
