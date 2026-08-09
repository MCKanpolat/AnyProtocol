# Transport development

AnyProtocol transport contracts are capability-oriented. `IMessagingProtocol`
contains only shared lifecycle and capability metadata; an implementation opts
into each operation it actually supports.

## Choose the smallest contracts

- `ISendTransport` — publish an envelope to a logical channel.
- `ISubscriptionTransport` — consume envelopes from a logical channel.
- `IRequestReplyTransport` — perform native request/reply.
- `IStreamingTransport` — perform native streaming.
- `INativeServerTransport` — the host owns server dispatch, so the transport
  does not expose send or subscription operations.

Most broker transports implement both `ISendTransport` and
`ISubscriptionTransport`. A request-only HTTP client can implement
`ISendTransport` and `IRequestReplyTransport`; it does not need to implement
`ISubscriptionTransport`. A native server adapter can implement only
`IMessagingProtocol`, `INativeServerTransport`, and any readiness contract it
supports.

```csharp
public sealed class ExampleRequestTransport :
    ISendTransport,
    IRequestReplyTransport
{
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeHeaders |
        TransportCapabilities.NativeRequestReply;

    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.None,
        Durability = TransportDurability.Volatile,
        SupportsNativeRequestReply = true,
        SupportsCancellation = true
    };

    public ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Example omitted.");

    public ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Example omitted.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`TransportCapabilities` remains the runtime feature description. The link
validator checks both the advertised capability and the corresponding
interface. For emulated request/reply or streaming, the transport must expose
both send and subscription contracts; native operations require their native
interface. Missing contracts fail during `LinkBuilder.Build`, before the bus
starts.

`INativeRequestReplyTransport` and `INativeStreamingTransport` remain as
compatibility aliases that inherit the new `IRequestReplyTransport` and
`IStreamingTransport` contracts.
