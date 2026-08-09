using System.Collections;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Encoder.MessagePack;
using MessagePack;
using Xunit;

namespace AnyProtocol.Encoder.MessagePack.Tests;

public sealed class MessagePackEnvelopeCodecTests
{
    [Fact]
    public void Round_trip_preserves_unicode_headers_and_arbitrary_body_bytes()
    {
        var envelope = new TransportEnvelope(
            new TestHeaders(
            [
                new("X-Name", "çağrı"),
                new("x-correlation", "値")
            ]),
            new byte[] { 0, 1, 2, 127, 128, 255 });

        var decoded = new MessagePackEnvelopeCodec().Decode(
            new MessagePackEnvelopeCodec().Encode(envelope));

        Assert.Equal("çağrı", decoded.Headers["x-name"]);
        Assert.Equal("値", decoded.Headers["X-CORRELATION"]);
        Assert.Equal(envelope.Body.ToArray(), decoded.Body.ToArray());
    }

    [Fact]
    public void Round_trip_preserves_an_empty_body_and_empty_headers()
    {
        var envelope = new TransportEnvelope(new TestHeaders([]), ReadOnlyMemory<byte>.Empty);

        var decoded = RoundTrip(envelope);

        Assert.Empty(decoded.Headers);
        Assert.Empty(decoded.Body.ToArray());
    }

    [Fact]
    public void Output_is_deterministic_for_the_same_logical_envelope()
    {
        var first = new TransportEnvelope(
            new TestHeaders([new("b", "2"), new("a", "1")]),
            new byte[] { 0, 255 });
        var second = new TransportEnvelope(
            new TestHeaders([new("a", "1"), new("b", "2")]),
            new byte[] { 0, 255 });

        Assert.Equal(
            new MessagePackEnvelopeCodec().Encode(first).ToArray(),
            new MessagePackEnvelopeCodec().Encode(second).ToArray());
    }

    [Fact]
    public void Encoded_shape_matches_the_stable_wire_fixture()
    {
        var envelope = new TransportEnvelope(
            new TestHeaders([new("b", "2"), new("a", "1")]),
            new byte[] { 0, 255 });

        var frame = new MessagePackEnvelopeCodec().Encode(envelope).ToArray();

        Assert.Equal(
            [
                0x93, 0x01, 0x82,
                0xa1, 0x61, 0xa1, 0x31,
                0xa1, 0x62, 0xa1, 0x32,
                0xc4, 0x02, 0x00, 0xff
            ],
            frame);
    }

    [Fact]
    public void Decode_rejects_an_unsupported_version()
    {
        var frame = new byte[] { 0x93, 0x02, 0x80, 0xc4, 0x00 };

        AssertInvalid(frame);
    }

    [Fact]
    public void Decode_rejects_malformed_and_truncated_input()
    {
        AssertInvalid(new byte[] { 0xc1 });
        AssertInvalid(new byte[] { 0x93, 0x01 });
        AssertInvalid(new byte[] { 0x93, 0x01, 0x80, 0xc4 });
        AssertInvalid(new byte[] { 0x93, 0x01, 0x80, 0xc0 });
    }

    [Fact]
    public void Decode_rejects_trailing_input()
    {
        var frame = new MessagePackEnvelopeCodec()
            .Encode(new TransportEnvelope(new TestHeaders([]), Array.Empty<byte>()))
            .ToArray()
            .Append((byte)0)
            .ToArray();

        AssertInvalid(frame);
    }

    [Fact]
    public void Decode_rejects_case_insensitive_duplicate_headers()
    {
        var frame =
        new byte[]
        {
            0x93, 0x01, 0x82,
            0xa1, 0x58, 0xa1, 0x31,
            0xa1, 0x78, 0xa1, 0x32,
            0xc4, 0x00
        };

        AssertInvalid(frame);
    }

    [Fact]
    public void Encode_and_decode_enforce_body_limit()
    {
        var options = new MessagePackEnvelopeCodecOptions
        {
            Limits = new EnvelopeCodecLimits
            {
                MaxBodySize = 2,
                MaxFrameSize = 1024
            }
        };
        var codec = new MessagePackEnvelopeCodec(options);
        var envelope = new TransportEnvelope(new TestHeaders([]), new byte[] { 1, 2, 3 });

        AssertInvalid(() => codec.Encode(envelope));

        var frame = new MessagePackEnvelopeCodec().Encode(envelope);
        AssertInvalid(() => codec.Decode(frame));
    }

    [Fact]
    public void Encode_and_decode_enforce_header_count_and_header_bytes_limits()
    {
        var countLimited = new MessagePackEnvelopeCodec(new()
        {
            Limits = new EnvelopeCodecLimits
            {
                MaxHeaderCount = 1,
                MaxHeaderBytes = 1024,
                MaxFrameSize = 1024,
                MaxBodySize = 1024
            }
        });
        var envelope = new TransportEnvelope(
            new TestHeaders([new("a", "1"), new("b", "2")]),
            Array.Empty<byte>());

        AssertInvalid(() => countLimited.Encode(envelope));
        var countLimitedFrame = new MessagePackEnvelopeCodec().Encode(envelope);
        AssertInvalid(() => countLimited.Decode(countLimitedFrame));

        var headerBytesLimited = new MessagePackEnvelopeCodec(new()
        {
            Limits = new EnvelopeCodecLimits
            {
                MaxHeaderCount = 2,
                MaxHeaderBytes = 3,
                MaxFrameSize = 1024,
                MaxBodySize = 1024
            }
        });
        var headerBytesEnvelope = new TransportEnvelope(
            new TestHeaders([new("a", "123")]),
            Array.Empty<byte>());

        AssertInvalid(() => headerBytesLimited.Encode(headerBytesEnvelope));

        var frame = new MessagePackEnvelopeCodec().Encode(headerBytesEnvelope);
        AssertInvalid(() => headerBytesLimited.Decode(frame));
    }

    [Fact]
    public void Encode_and_decode_enforce_complete_frame_limit()
    {
        var envelope = new TransportEnvelope(new TestHeaders([]), new byte[] { 1, 2, 3 });
        var frame = new MessagePackEnvelopeCodec().Encode(envelope);
        var codec = new MessagePackEnvelopeCodec(new()
        {
            Limits = new EnvelopeCodecLimits
            {
                MaxFrameSize = frame.Length - 1,
                MaxBodySize = 3,
                MaxHeaderBytes = 1024
            }
        });

        AssertInvalid(() => codec.Encode(envelope));
        AssertInvalid(() => codec.Decode(frame));
    }

    [Fact]
    public void Decode_uses_the_public_dto_schema_without_deserializing_application_data()
    {
        var dto = new MessagePackEnvelopeFrame
        {
            Version = MessagePackEnvelopeFrame.CurrentVersion,
            Headers = new Dictionary<string, string> { ["content-type"] = "application/octet-stream" },
            Body = [0, 255, 3]
        };
        var frame = MessagePackSerializer.Serialize(dto);

        var decoded = new MessagePackEnvelopeCodec().Decode(frame);

        Assert.Equal("application/octet-stream", decoded.Headers["CONTENT-TYPE"]);
        Assert.Equal([0, 255, 3], decoded.Body.ToArray());
    }

    private static TransportEnvelope RoundTrip(TransportEnvelope envelope)
    {
        var codec = new MessagePackEnvelopeCodec();
        return codec.Decode(codec.Encode(envelope));
    }

    private static void AssertInvalid(ReadOnlyMemory<byte> frame)
        => Assert.Throws<InvalidDataException>(
            () => new MessagePackEnvelopeCodec().Decode(frame));

    private static void AssertInvalid(Action action)
        => Assert.Throws<InvalidDataException>(action);

    private sealed class TestHeaders : IMessageHeaders
    {
        private readonly Dictionary<string, string> _headers;

        public TestHeaders(IEnumerable<KeyValuePair<string, string>> headers)
            => _headers = new(headers, StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get => _headers.GetValueOrDefault(key);
            set
            {
                if (value is null)
                {
                    _headers.Remove(key);
                }
                else
                {
                    _headers[key] = value;
                }
            }
        }

        string IReadOnlyDictionary<string, string>.this[string key] => _headers[key];

        public IEnumerable<string> Keys => _headers.Keys;

        public IEnumerable<string> Values => _headers.Values;

        public int Count => _headers.Count;

        public void Set(string key, string value) => _headers[key] = value;

        public bool Remove(string key) => _headers.Remove(key);

        public bool ContainsKey(string key) => _headers.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _headers.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
