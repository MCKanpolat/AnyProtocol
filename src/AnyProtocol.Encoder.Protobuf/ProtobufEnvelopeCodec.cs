using System.Collections;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Encoder.Protobuf.Generated;
using Google.Protobuf;

namespace AnyProtocol.Encoder.Protobuf;

/// <summary>
/// Encodes and decodes the versioned Protobuf transport envelope.
/// </summary>
/// <remarks>
/// Unknown Protobuf fields are ignored by design so that newer envelope writers can
/// add fields without breaking older readers. Envelope versions remain strict.
/// The body is opaque to this codec and must already have been processed by the
/// application serializer.
/// </remarks>
public sealed class ProtobufEnvelopeCodec : IEnvelopeCodec
{
    private const uint CurrentVersion = 1;
    private const uint LengthDelimitedWireType = 2;

    private readonly EnvelopeCodecLimits _limits;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufEnvelopeCodec"/> class.
    /// </summary>
    /// <param name="maxFrameBytes">The maximum encoded frame size.</param>
    /// <param name="maxBodyBytes">The maximum opaque body size.</param>
    /// <param name="maxHeaderCount">The maximum number of header entries.</param>
    /// <param name="maxHeaderBytes">The maximum combined UTF-8 size of header keys and values.</param>
    public ProtobufEnvelopeCodec(
        int maxFrameBytes = EnvelopeCodecLimits.DefaultMaxFrameSize,
        int maxBodyBytes = EnvelopeCodecLimits.DefaultMaxBodySize,
        int maxHeaderCount = EnvelopeCodecLimits.DefaultMaxHeaderCount,
        int maxHeaderBytes = EnvelopeCodecLimits.DefaultMaxHeaderBytes)
        : this(new ProtobufEnvelopeCodecOptions
        {
            Limits = new EnvelopeCodecLimits
            {
                MaxFrameSize = maxFrameBytes,
                MaxBodySize = maxBodyBytes,
                MaxHeaderCount = maxHeaderCount,
                MaxHeaderBytes = maxHeaderBytes
            }
        })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtobufEnvelopeCodec"/> class.
    /// </summary>
    /// <param name="options">The codec options.</param>
    public ProtobufEnvelopeCodec(ProtobufEnvelopeCodecOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _limits = options.Limits;
    }

    /// <summary>
    /// Encodes a transport envelope into a Protobuf frame.
    /// </summary>
    /// <param name="envelope">The transport envelope to encode.</param>
    /// <returns>The encoded Protobuf frame.</returns>
    public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Body.Length > _limits.MaxBodySize)
        {
            throw new InvalidDataException($"The envelope body exceeds the {_limits.MaxBodySize}-byte limit.");
        }

        var message = new EnvelopeFrame
        {
            Version = CurrentVersion,
            Body = ByteString.CopyFrom(envelope.Body.Span)
        };
        var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        var headerCount = 0;

        foreach (var header in envelope.Headers)
        {
            headerCount++;
            ValidateHeader(
                header.Key,
                header.Value,
                headerKeys,
                ref headerBytes,
                headerCount);
            message.Headers.Add(header.Key, header.Value);
        }

        var frameBytes = message.ToByteArray();
        if (frameBytes.Length > _limits.MaxFrameSize)
        {
            throw new InvalidDataException($"The encoded envelope exceeds the {_limits.MaxFrameSize}-byte frame limit.");
        }

        return frameBytes;
    }

    /// <summary>
    /// Decodes a Protobuf frame into a transport envelope.
    /// </summary>
    /// <param name="frame">The encoded Protobuf frame.</param>
    /// <returns>The decoded transport envelope.</returns>
    public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > _limits.MaxFrameSize)
        {
            throw new InvalidDataException($"The encoded envelope exceeds the {_limits.MaxFrameSize}-byte frame limit.");
        }

        var frameBytes = frame.ToArray();
        try
        {
            ValidateWireLimits(frameBytes);
            var message = EnvelopeFrame.Parser.ParseFrom(frameBytes);

            if (message.Version != CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported AnyProtocol envelope version '{message.Version}'.");
            }

            if (message.Body.Length > _limits.MaxBodySize)
            {
                throw new InvalidDataException($"The envelope body exceeds the {_limits.MaxBodySize}-byte limit.");
            }

            if (message.Headers.Count > _limits.MaxHeaderCount)
            {
                throw new InvalidDataException($"The envelope contains more than the {_limits.MaxHeaderCount}-header limit.");
            }

            var headers = new ProtobufHeaders();
            var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var headerBytes = 0;
            var headerCount = 0;
            foreach (var header in message.Headers)
            {
                headerCount++;
                ValidateHeader(
                    header.Key,
                    header.Value,
                    headerKeys,
                    ref headerBytes,
                    headerCount);
                headers.Set(header.Key, header.Value);
            }

            return new TransportEnvelope(headers, message.Body.ToByteArray());
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidDataException("The Protobuf envelope is malformed or truncated.", exception);
        }
    }

    private void ValidateWireLimits(byte[] frame)
    {
        using var input = new CodedInputStream(frame);
        var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        var headerCount = 0;

        while (true)
        {
            var tag = input.ReadTag();
            if (tag == 0)
            {
                break;
            }

            var fieldNumber = tag >> 3;
            var wireType = tag & 7;
            if (fieldNumber == 2 && wireType == LengthDelimitedWireType)
            {
                ValidateHeaderEntry(input.ReadBytes(), headerKeys, ref headerBytes, ref headerCount);
                continue;
            }

            if (fieldNumber == 3 && wireType == LengthDelimitedWireType)
            {
                var body = input.ReadBytes();
                if (body.Length > _limits.MaxBodySize)
                {
                    throw new InvalidDataException($"The envelope body exceeds the {_limits.MaxBodySize}-byte limit.");
                }

                continue;
            }

            // Unknown fields, including fields added by newer writers, are intentionally ignored.
            input.SkipLastField();
        }

        if (headerCount > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException($"The envelope contains more than the {_limits.MaxHeaderCount}-header limit.");
        }
    }

    private void ValidateHeaderEntry(
        ByteString entry,
        HashSet<string> headerKeys,
        ref int headerBytes,
        ref int headerCount)
    {
        using var input = entry.CreateCodedInput();
        var key = string.Empty;
        var value = string.Empty;

        while (true)
        {
            var tag = input.ReadTag();
            if (tag == 0)
            {
                break;
            }

            var fieldNumber = tag >> 3;
            var wireType = tag & 7;
            if (fieldNumber == 1 && wireType == LengthDelimitedWireType)
            {
                key = input.ReadString();
                continue;
            }

            if (fieldNumber == 2 && wireType == LengthDelimitedWireType)
            {
                value = input.ReadString();
                continue;
            }

            input.SkipLastField();
        }

        headerCount++;
        ValidateHeader(key, value, headerKeys, ref headerBytes, headerCount);
    }

    private void ValidateHeader(
        string? key,
        string? value,
        HashSet<string> headerKeys,
        ref int headerBytes,
        int headerCount)
    {
        if (headerCount > _limits.MaxHeaderCount)
        {
            throw new InvalidDataException($"The envelope contains more than the {_limits.MaxHeaderCount}-header limit.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidDataException("Envelope header names must not be null, empty, or whitespace.");
        }

        if (value is null)
        {
            throw new InvalidDataException("Envelope header values must not be null.");
        }

        if (!headerKeys.Add(key))
        {
            throw new InvalidDataException($"The envelope contains duplicate header '{key}' (case-insensitive).");
        }

        var headerSize = checked(Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value));
        if (headerSize > _limits.MaxHeaderBytes - headerBytes)
        {
            throw new InvalidDataException($"The envelope headers exceed the {_limits.MaxHeaderBytes}-byte limit.");
        }

        headerBytes += headerSize;
    }

    private sealed class ProtobufHeaders : IMessageHeaders
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get => _headers.GetValueOrDefault(key);
            set
            {
                if (value is null)
                {
                    _headers.Remove(key);
                    return;
                }

                _headers[key] = value;
            }
        }

        string IReadOnlyDictionary<string, string>.this[string key] => _headers[key];

        public IEnumerable<string> Keys => _headers.Keys;

        public IEnumerable<string> Values => _headers.Values;

        public int Count => _headers.Count;

        public bool ContainsKey(string key) => _headers.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _headers.TryGetValue(key, out value!);

        public void Set(string key, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            _headers[key] = value;
        }

        public bool Remove(string key) => _headers.Remove(key);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

}
