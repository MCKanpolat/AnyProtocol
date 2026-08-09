using System.Buffers;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using MessagePack;

namespace AnyProtocol.Encoder.MessagePack;

/// <summary>
/// Encodes transport envelopes using a fixed MessagePack array schema.
/// </summary>
/// <remarks>
/// The wire shape is <c>[version, headers, body]</c>, where the first item is an
/// integer, the second item is a string-to-string map, and the third item is a
/// binary value. The body is opaque to this codec and is never passed to an
/// application serializer.
/// </remarks>
public sealed class MessagePackEnvelopeCodec : IEnvelopeCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly EnvelopeCodecLimits _limits;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessagePackEnvelopeCodec"/> class.
    /// </summary>
    /// <param name="options">The codec options.</param>
    public MessagePackEnvelopeCodec(MessagePackEnvelopeCodecOptions? options = null)
    {
        var resolvedOptions = options ?? MessagePackEnvelopeCodecOptions.Default;
        resolvedOptions.Validate();
        _limits = resolvedOptions.Limits;
    }

    /// <summary>
    /// Encodes a transport envelope into a MessagePack frame.
    /// </summary>
    /// <param name="envelope">The transport envelope to encode.</param>
    /// <returns>The encoded frame.</returns>
    public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Headers);

        if (envelope.Headers.Count > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException(
                $"The envelope contains more than {_limits.MaxHeaderCount} headers.");
        }

        var headers = envelope.Headers.ToArray();
        ValidateHeaders(headers);
        ValidateBodyLength(envelope.Body.Length);

        var frame = new MessagePackEnvelopeFrame
        {
            Version = MessagePackEnvelopeFrame.CurrentVersion,
            Headers = headers
                .OrderBy(static header => header.Key, StringComparer.Ordinal)
                .ToDictionary(static header => header.Key, static header => header.Value, StringComparer.Ordinal),
            Body = envelope.Body.ToArray()
        };

        try
        {
            var bytes = MessagePackSerializer.Serialize(frame, SerializerOptions);
            if (bytes.Length > _limits.MaxFrameSize)
            {
                throw new InvalidDataException(
                    $"The frame exceeds the maximum size of {_limits.MaxFrameSize} bytes.");
            }

            return bytes;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (IsMessagePackFailure(exception))
        {
            throw new InvalidDataException("The MessagePack envelope could not be encoded.", exception);
        }
    }

    /// <summary>
    /// Decodes a MessagePack frame into a transport envelope.
    /// </summary>
    /// <param name="frame">The encoded frame.</param>
    /// <returns>The decoded transport envelope.</returns>
    public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > _limits.MaxFrameSize)
        {
            throw new InvalidDataException(
                $"The frame exceeds the maximum size of {_limits.MaxFrameSize} bytes.");
        }

        try
        {
            ValidateFrameShape(frame);
            var decoded = MessagePackSerializer.Deserialize<MessagePackEnvelopeFrame>(
                frame,
                SerializerOptions);

            if (decoded is null || decoded.Headers is null || decoded.Body is null)
            {
                throw new InvalidDataException("The MessagePack envelope contains a null field.");
            }

            if (decoded.Version != MessagePackEnvelopeFrame.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported AnyProtocol envelope version '{decoded.Version}'.");
            }

            var decodedHeaders = decoded.Headers.ToArray();
            ValidateHeaders(decodedHeaders);
            ValidateBodyLength(decoded.Body.Length);

            var headers = new MessageHeaders(decodedHeaders);
            return new TransportEnvelope(headers, decoded.Body);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (IsMessagePackFailure(exception))
        {
            throw new InvalidDataException("The MessagePack envelope is malformed.", exception);
        }
    }

    private void ValidateFrameShape(ReadOnlyMemory<byte> frame)
    {
        var reader = new MessagePackReader(frame);

        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            throw new InvalidDataException("The MessagePack envelope must be an array.");
        }

        if (reader.ReadArrayHeader() != 3)
        {
            throw new InvalidDataException("The MessagePack envelope must contain exactly three fields.");
        }

        if (reader.NextMessagePackType != MessagePackType.Integer)
        {
            throw new InvalidDataException("The MessagePack envelope version must be an integer.");
        }

        var version = reader.ReadInt64();
        if (version != MessagePackEnvelopeFrame.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported AnyProtocol envelope version '{version}'.");
        }

        if (reader.NextMessagePackType != MessagePackType.Map)
        {
            throw new InvalidDataException("The MessagePack envelope headers must be a map.");
        }

        var headerCount = reader.ReadMapHeader();
        if (headerCount > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException(
                $"The envelope contains more than {_limits.MaxHeaderCount} headers.");
        }

        var headerBytes = 0L;
        var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headerCount; index++)
        {
            if (reader.NextMessagePackType != MessagePackType.String)
            {
                throw new InvalidDataException("Envelope header keys must be strings.");
            }

            var key = reader.ReadString();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException("Envelope header keys must not be blank.");
            }

            if (!headerKeys.Add(key))
            {
                throw new InvalidDataException($"Duplicate header key '{key}'.");
            }

            if (reader.NextMessagePackType != MessagePackType.String)
            {
                throw new InvalidDataException("Envelope header values must be strings.");
            }

            var value = reader.ReadString();
            if (value is null)
            {
                throw new InvalidDataException("Envelope header values must not be null.");
            }

            headerBytes = checked(headerBytes + Utf8.GetByteCount(key) + Utf8.GetByteCount(value));
            if (headerBytes > _limits.MaxHeaderBytes)
            {
                throw new InvalidDataException(
                    $"The envelope headers exceed the maximum size of {_limits.MaxHeaderBytes} bytes.");
            }
        }

        if (reader.NextMessagePackType != MessagePackType.Binary)
        {
            throw new InvalidDataException("The MessagePack envelope body must be binary data.");
        }

        var body = reader.ReadBytes();
        if (body is null)
        {
            throw new InvalidDataException("The MessagePack envelope body must not be null.");
        }

        if (body.Value.Length > _limits.MaxBodySize)
        {
            throw new InvalidDataException(
                $"The envelope body exceeds the maximum size of {_limits.MaxBodySize} bytes.");
        }

        if (!reader.End)
        {
            throw new InvalidDataException("The envelope contains trailing data.");
        }
    }

    private void ValidateHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (headers.Count > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException(
                $"The envelope contains more than {_limits.MaxHeaderCount} headers.");
        }

        var headerBytes = 0L;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key) || header.Value is null)
            {
                throw new InvalidDataException(
                    "Envelope header keys and values must be non-null and keys must not be blank.");
            }

            if (!keys.Add(header.Key))
            {
                throw new InvalidDataException($"Duplicate header key '{header.Key}'.");
            }

            headerBytes = checked(
                headerBytes + Utf8.GetByteCount(header.Key) + Utf8.GetByteCount(header.Value));
            if (headerBytes > _limits.MaxHeaderBytes)
            {
                throw new InvalidDataException(
                    $"The envelope headers exceed the maximum size of {_limits.MaxHeaderBytes} bytes.");
            }
        }
    }

    private void ValidateBodyLength(int bodyLength)
    {
        if (bodyLength > _limits.MaxBodySize)
        {
            throw new InvalidDataException(
                $"The envelope body exceeds the maximum size of {_limits.MaxBodySize} bytes.");
        }
    }

    private static bool IsMessagePackFailure(Exception exception) => exception is
        MessagePackSerializationException or
        EndOfStreamException or
        InvalidOperationException or
        ArgumentException or
        OverflowException;

}
