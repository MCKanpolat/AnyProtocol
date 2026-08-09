# Envelope codecs

AnyProtocol keeps application payload serialization separate from transport envelope encoding:

```text
application message -> IMessageSerializer -> TransportEnvelope -> IEnvelopeCodec -> transport frame
```

`IMessageSerializer` chooses how an application message is represented in `TransportEnvelope.Body`. The envelope codec only owns headers and the opaque body bytes. For example, a JSON serializer can be used with a MessagePack envelope codec, and a MessagePack serializer can be used with the binary envelope codec.

## Codec choices

| Codec | Use when |
|---|---|
| `BinaryEnvelopeCodec` | The default, backwards-compatible AnyProtocol wire format is the right choice. |
| `MessagePackEnvelopeCodec` | A compact internal envelope is preferred and both endpoints are controlled by the same deployment. |
| `ProtobufEnvelopeCodec` | The envelope is a long-lived or cross-language wire contract. The body remains opaque application data. |
| `CompressedEnvelopeCodec` | Frames contain large or repetitive data. It decorates another codec and applies compression after envelope encoding. |

All codecs validate the envelope version and common size limits. Header keys are case-insensitive, header values are strings, and malformed or trailing input is rejected. Compression also bounds decompression size and ratio.

## Selecting a codec per transport

Codec selection belongs to the transport boundary. It is not a global serializer setting. gRPC and ZeroMQ keep their existing constructors, which default to `BinaryEnvelopeCodec`, and also accept an `IEnvelopeCodec`:

```csharp
var codec = new CompressedEnvelopeCodec(new BinaryEnvelopeCodec());

var grpc = new GrpcMessagingProtocol(
    channel,
    codec,
    disposeChannel: true);

var zeroMq = new ZeroMqMessagingProtocol(
    new ZeroMqProtocolOptions
    {
        Role = ZeroMqRole.Client,
        RouterEndpoint = "tcp://orders.example:5555",
        PublisherEndpoint = "tcp://orders.example:5556"
    },
    codec);
```

The sender and receiver at a transport boundary must use compatible codecs. A different codec configuration is rejected as invalid transport data; it must not be treated as a different application message type.

The ASP.NET Core gRPC service accepts an `IEnvelopeCodec` through dependency injection. Register the same codec configuration for the server and client transport:

```csharp
builder.Services.AddSingleton<IEnvelopeCodec>(codec);
builder.Services.AddAnyProtocolGrpc();
```

REST, Kafka, RabbitMQ, and InMemory keep their existing transport-specific envelope mapping and do not require an envelope codec unless a future transport explicitly introduces raw envelope frames.

## Wire contracts

The binary codec uses little-endian integers and preserves the existing version-1 layout: magic marker, version, header count, UTF-8 header key/value pairs, body length, and body bytes.

The MessagePack codec uses an explicit envelope DTO with numeric keys for version, headers, and opaque body bytes. The Protobuf codec uses the checked-in `EnvelopeFrame` schema under `src/AnyProtocol.Encoder.Protobuf/Protos/envelope.proto`. Compression has its own small wrapper containing a magic marker, wrapper version, algorithm, original length, and bounded payload.
