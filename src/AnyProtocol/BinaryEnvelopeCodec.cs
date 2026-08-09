using System.Buffers.Binary;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the binary envelope codec implementation used by AnyProtocol applications.
/// </summary>
public sealed class BinaryEnvelopeCodec : IEnvelopeCodec
{
    private const uint Magic = 0x4B4E4C43;
    private const byte Version = 1;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly EnvelopeCodecLimits _limits;

    /// <summary>
    /// Initializes a new instance of the BinaryEnvelopeCodec class.
    /// </summary>
    /// <param name="limits">The limits applied while encoding and decoding.</param>
    public BinaryEnvelopeCodec(EnvelopeCodecLimits? limits = null)
    {
        _limits = limits ?? EnvelopeCodecLimits.Default;
        _limits.Validate();
    }

    /// <summary>
    /// Encodes  for transport.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <returns>The result of the encode operation.</returns>
    public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Headers);

        var headers = envelope.Headers.ToArray();
        ValidateHeaders(headers);
        if (envelope.Body.Length > _limits.MaxBodySize)
        {
            throw new InvalidDataException($"The envelope body exceeds the maximum size of {_limits.MaxBodySize} bytes.");
        }

        var headerBytes = headers.Sum(static header =>
            checked(Utf8.GetByteCount(header.Key) + Utf8.GetByteCount(header.Value)));
        var frameLength = checked(
            sizeof(uint) + sizeof(byte) + sizeof(int) +
            headers.Sum(static header =>
                checked(sizeof(int) + Utf8.GetByteCount(header.Key) +
                        sizeof(int) + Utf8.GetByteCount(header.Value))) +
            sizeof(int) + envelope.Body.Length);
        if (headerBytes > _limits.MaxHeaderBytes || frameLength > _limits.MaxFrameSize)
        {
            throw new InvalidDataException("The envelope exceeds the configured codec limits.");
        }

        var frame = new byte[frameLength];
        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset, sizeof(uint)), Magic);
        offset += sizeof(uint);
        frame[offset++] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, sizeof(int)), headers.Length);
        offset += sizeof(int);
        foreach (var header in headers)
        {
            offset = WriteString(frame, offset, header.Key);
            offset = WriteString(frame, offset, header.Value);
        }

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset, sizeof(int)), envelope.Body.Length);
        offset += sizeof(int);
        envelope.Body.Span.CopyTo(frame.AsSpan(offset));
        return frame;
    }

    /// <summary>
    /// Decodes  from transport data.
    /// </summary>
    /// <param name="frame">The frame to process.</param>
    /// <returns>The result of the decode operation.</returns>
    public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > _limits.MaxFrameSize)
        {
            throw new InvalidDataException($"The frame exceeds the maximum size of {_limits.MaxFrameSize} bytes.");
        }

        var bytes = frame.Span;
        var offset = 0;
        if (!TryReadUInt32(bytes, ref offset, out var magic) || magic != Magic)
        {
            throw new InvalidDataException("The frame is not a AnyProtocol envelope.");
        }

        if (!TryReadByte(bytes, ref offset, out var version) || version != Version)
        {
            throw new InvalidDataException($"Unsupported AnyProtocol envelope version '{version}'.");
        }

        if (!TryReadInt32(bytes, ref offset, out var headerCount) ||
            headerCount < 0 || headerCount > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException($"Invalid header count '{headerCount}'.");
        }

        var headers = new MessageHeaders();
        var headerBytes = 0;
        var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headerCount; index++)
        {
            var key = ReadString(bytes, ref offset, ref headerBytes);
            var value = ReadString(bytes, ref offset, ref headerBytes);
            if (!headerKeys.Add(key))
            {
                throw new InvalidDataException($"Duplicate header key '{key}'.");
            }

            headers.Set(key, value);
        }

        if (!TryReadInt32(bytes, ref offset, out var bodyLength) ||
            bodyLength < 0 || bodyLength > _limits.MaxBodySize ||
            bodyLength > bytes.Length - offset)
        {
            throw new InvalidDataException($"Invalid body length '{bodyLength}'.");
        }

        var body = bytes.Slice(offset, bodyLength).ToArray();
        offset += bodyLength;
        if (offset != bytes.Length)
        {
            throw new InvalidDataException("The envelope contains trailing data.");
        }

        return new TransportEnvelope(headers, body);
    }

    private void ValidateHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (headers.Count > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException($"The envelope contains more than {_limits.MaxHeaderCount} headers.");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key) || header.Value is null)
            {
                throw new InvalidDataException("Envelope header keys and values must be non-null and keys must not be blank.");
            }

            if (!keys.Add(header.Key))
            {
                throw new InvalidDataException($"Duplicate header key '{header.Key}'.");
            }
        }
    }

    private static int WriteString(byte[] destination, int offset, string value)
    {
        var bytes = Utf8.GetBytes(value);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, sizeof(int)), bytes.Length);
        offset += sizeof(int);
        bytes.CopyTo(destination, offset);
        return offset + bytes.Length;
    }

    private string ReadString(ReadOnlySpan<byte> frame, ref int offset, ref int totalBytes)
    {
        if (!TryReadInt32(frame, ref offset, out var length) ||
            length < 0 || length > _limits.MaxHeaderBytes - totalBytes ||
            length > frame.Length - offset)
        {
            throw new InvalidDataException($"Invalid header value length '{length}'.");
        }

        try
        {
            var value = Utf8.GetString(frame.Slice(offset, length));
            offset += length;
            totalBytes += length;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The envelope contains invalid UTF-8.", exception);
        }
    }

    private static bool TryReadByte(ReadOnlySpan<byte> frame, ref int offset, out byte value)
    {
        if (offset >= frame.Length)
        {
            value = default;
            return false;
        }

        value = frame[offset++];
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> frame, ref int offset, out uint value)
    {
        if (frame.Length - offset < sizeof(uint))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> frame, ref int offset, out int value)
    {
        if (frame.Length - offset < sizeof(int))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }
}
