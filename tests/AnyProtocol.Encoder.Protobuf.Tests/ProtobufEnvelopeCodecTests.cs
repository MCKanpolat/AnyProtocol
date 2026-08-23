using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Protobuf;
using AnyProtocol.Encoder.Protobuf.Generated;
using Google.Protobuf;
using Xunit;

namespace AnyProtocol.Encoder.Protobuf.Tests;

public sealed class ProtobufEnvelopeCodecTests
{
    [Fact]
    public void Constructor_rejects_null_options_and_invalid_limits()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ProtobufEnvelopeCodec((ProtobufEnvelopeCodecOptions)null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProtobufEnvelopeCodec(maxFrameBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProtobufEnvelopeCodec(maxBodyBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProtobufEnvelopeCodec(maxHeaderCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProtobufEnvelopeCodec(maxHeaderBytes: -1));
    }

    [Fact]
    public void Encode_and_decode_reject_blank_header_keys()
    {
        var codec = new ProtobufEnvelopeCodec();

        Assert.Throws<InvalidDataException>(
            () => codec.Encode(
                new TransportEnvelope(
                    new TestHeaders((" ", "value")),
                    ReadOnlyMemory<byte>.Empty)));

        var frame = new EnvelopeFrame { Version = 1 };
        frame.Headers.Add(string.Empty, "value");

        Assert.Throws<InvalidDataException>(() => codec.Decode(frame.ToByteArray()));
    }

    [Fact]
    public void Encode_rejects_a_serialized_frame_above_the_frame_limit()
    {
        var codec = new ProtobufEnvelopeCodec(
            maxFrameBytes: 2,
            maxBodyBytes: 2,
            maxHeaderBytes: 2);

        Assert.Throws<InvalidDataException>(
            () => codec.Encode(
                new TransportEnvelope(
                    new TestHeaders(),
                    new byte[] { 1 })));
    }

    [Fact]
    public void Decode_skips_unknown_header_entry_fields_and_exposes_header_operations()
    {
        // Map entry: key = "k", value = "v", plus an unknown varint field.
        var entry = new byte[]
        {
            0x0A, 0x01, (byte)'k',
            0x12, 0x01, (byte)'v',
            0x18, 0x01
        };
        var frame = new byte[2 + 2 + entry.Length + 2];
        frame[0] = 0x08;
        frame[1] = 0x01;
        frame[2] = 0x12;
        frame[3] = (byte)entry.Length;
        Buffer.BlockCopy(entry, 0, frame, 4, entry.Length);
        frame[^2] = 0x1A;
        frame[^1] = 0x00;

        var headers = new ProtobufEnvelopeCodec().Decode(frame).Headers;

        Assert.Single(headers);
        Assert.True(headers.ContainsKey("K"));
        Assert.True(headers.TryGetValue("K", out var value));
        Assert.Equal("v", value);
        Assert.Equal(["v"], headers.Values);
        Assert.Equal(["k"], headers.Keys);
        Assert.Equal("v", ((IReadOnlyDictionary<string, string>)headers)["k"]);

        headers["k"] = null;
        Assert.Empty(headers);
        headers["new"] = "value";
        Assert.True(headers.Remove("NEW"));
        Assert.False(headers.Remove("missing"));
        Assert.Empty(headers.ToArray());
        Assert.Empty(((System.Collections.IEnumerable)headers).Cast<object>());
    }

    [Fact]
    public void Round_trip_preserves_opaque_body_and_headers()
    {
        var codec = new ProtobufEnvelopeCodec();
        var envelope = new TransportEnvelope(
            new TestHeaders(
                ("Content-Type", "application/octet-stream"),
                ("X-\u00DCnicode", "\u0437\u043d\u0430\u0447\u0435\u043d\u0438\u0435")),
            new byte[] { 0, 1, 2, 255 });

        var decoded = codec.Decode(codec.Encode(envelope));

        Assert.Equal(envelope.Body.ToArray(), decoded.Body.ToArray());
        Assert.Equal("application/octet-stream", decoded.Headers["content-type"]);
        Assert.Equal("\u0437\u043d\u0430\u0447\u0435\u043d\u0438\u0435", decoded.Headers["x-\u00FCnicode"]);
    }

    [Fact]
    public void Encode_rejects_case_insensitive_duplicate_headers()
    {
        var codec = new ProtobufEnvelopeCodec();

        var exception = Assert.Throws<InvalidDataException>(
            () => codec.Encode(new TransportEnvelope(
                new TestHeaders(("X-Request-Id", "one"), ("x-request-id", "two")),
                Array.Empty<byte>())));

        Assert.Contains("duplicate header", exception.Message);
    }

    [Fact]
    public void Decode_rejects_case_insensitive_duplicate_headers()
    {
        var frame = new EnvelopeFrame
        {
            Version = 1,
            Body = ByteString.Empty
        };
        frame.Headers.Add("X-Request-Id", "one");
        frame.Headers.Add("x-request-id", "two");

        var exception = Assert.Throws<InvalidDataException>(
            () => new ProtobufEnvelopeCodec().Decode(frame.ToByteArray()));

        Assert.Contains("duplicate header", exception.Message);
    }

    [Fact]
    public void Decode_rejects_unsupported_version()
    {
        var frame = new EnvelopeFrame { Version = 2 };

        var exception = Assert.Throws<InvalidDataException>(
            () => new ProtobufEnvelopeCodec().Decode(frame.ToByteArray()));

        Assert.Contains("Unsupported", exception.Message);
    }

    [Fact]
    public void Decode_ignores_unknown_fields()
    {
        var frame = new EnvelopeFrame
        {
            Version = 1,
            Body = ByteString.CopyFromUtf8("body")
        };
        var frameBytes = frame.ToByteArray();
        var withUnknownField = new byte[frameBytes.Length + 3];
        Buffer.BlockCopy(frameBytes, 0, withUnknownField, 0, frameBytes.Length);
        withUnknownField[^3] = 0xA0;
        withUnknownField[^2] = 0x06;
        withUnknownField[^1] = 0x01;

        var decoded = new ProtobufEnvelopeCodec().Decode(withUnknownField);

        Assert.Equal("body", Encoding.UTF8.GetString(decoded.Body.Span));
    }

    [Fact]
    public void Decode_accepts_the_independently_defined_version_one_fixture()
    {
        // EnvelopeFrame { version: 1, body: "body" } encoded with proto3 wire rules.
        var decoded = new ProtobufEnvelopeCodec().Decode(
            Bytes(0x08, 0x01, 0x1A, 0x04, 0x62, 0x6F, 0x64, 0x79));

        Assert.Equal("body", Encoding.UTF8.GetString(decoded.Body.Span));
        Assert.Empty(decoded.Headers);
    }

    [Fact]
    public void Decode_rejects_malformed_and_truncated_input()
    {
        var codec = new ProtobufEnvelopeCodec();

        Assert.Throws<InvalidDataException>(() => codec.Decode(new byte[] { 0x08 }));
        Assert.Throws<InvalidDataException>(() => codec.Decode(new byte[] { 0x0A, 0x05, 0x01 }));
    }

    [Fact]
    public void Encode_and_decode_enforce_frame_body_and_header_limits()
    {
        var codec = new ProtobufEnvelopeCodec(
            maxFrameBytes: 32,
            maxBodyBytes: 4,
            maxHeaderCount: 1,
            maxHeaderBytes: 8);

        Assert.Throws<InvalidDataException>(
            () => codec.Encode(new TransportEnvelope(new TestHeaders(("a", "b"), ("c", "d")), Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(
            () => codec.Encode(new TransportEnvelope(
                new TestHeaders(),
                new byte[] { 1, 2, 3, 4, 5 })));
        Assert.Throws<InvalidDataException>(
            () => codec.Encode(new TransportEnvelope(new TestHeaders(("long", "value")), Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(
            () => new ProtobufEnvelopeCodec(maxBodyBytes: 2).Decode(
                new EnvelopeFrame { Version = 1, Body = ByteString.CopyFrom([1, 2, 3]) }.ToByteArray()));
        Assert.Throws<InvalidDataException>(
            () => new ProtobufEnvelopeCodec(maxFrameBytes: 2, maxBodyBytes: 2).Decode(
                new byte[] { 0, 0, 0 }));
    }

    private static ReadOnlyMemory<byte> Bytes(params byte[] bytes) => bytes;

    private sealed class TestHeaders : IMessageHeaders
    {
        private readonly Dictionary<string, string> _headers;

        public TestHeaders(params (string Key, string Value)[] headers)
        {
            _headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in headers)
            {
                _headers.Add(key, value);
            }
        }

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

        public bool ContainsKey(string key) => _headers.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _headers.TryGetValue(key, out value!);

        public void Set(string key, string value) => _headers[key] = value;

        public bool Remove(string key) => _headers.Remove(key);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
