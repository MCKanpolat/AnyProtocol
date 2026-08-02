using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the binary envelope codec implementation used by AnyProtocol applications.
/// </summary>
public sealed class BinaryEnvelopeCodec : IMessageEncoder, IMessageDecoder
{
    private const uint Magic = 0x4B4E4C43;
    private const byte Version = 1;
    private const int MaxHeaderCount = 1024;
    private const int MaxHeaderBytes = 1024 * 1024;

    /// <summary>
    /// Encodes  for transport.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <returns>The result of the encode operation.</returns>
    public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(envelope.Headers.Count);

        foreach (var header in envelope.Headers)
        {
            WriteString(writer, header.Key);
            WriteString(writer, header.Value);
        }

        writer.Write(envelope.Body.Length);
        writer.Write(envelope.Body.Span);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Decodes  from transport data.
    /// </summary>
    /// <param name="frame">The frame to process.</param>
    /// <returns>The result of the decode operation.</returns>
    public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
    {
        using var stream = new MemoryStream(frame.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("The frame is not a AnyProtocol envelope.");
        }

        var version = reader.ReadByte();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported AnyProtocol envelope version '{version}'.");
        }

        var headerCount = reader.ReadInt32();
        if (headerCount is < 0 or > MaxHeaderCount)
        {
            throw new InvalidDataException($"Invalid header count '{headerCount}'.");
        }

        var headers = new MessageHeaders();
        var headerBytes = 0;
        for (var index = 0; index < headerCount; index++)
        {
            var key = ReadString(reader, ref headerBytes);
            var value = ReadString(reader, ref headerBytes);
            headers.Set(key, value);
        }

        var bodyLength = reader.ReadInt32();
        if (bodyLength < 0 || bodyLength > stream.Length - stream.Position)
        {
            throw new InvalidDataException($"Invalid body length '{bodyLength}'.");
        }

        var body = reader.ReadBytes(bodyLength);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The envelope contains trailing data.");
        }

        return new TransportEnvelope(headers, body);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, ref int totalBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || totalBytes + length > MaxHeaderBytes)
        {
            throw new InvalidDataException($"Invalid header value length '{length}'.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("The envelope ended inside a header.");
        }

        totalBytes += length;
        return Encoding.UTF8.GetString(bytes);
    }
}
