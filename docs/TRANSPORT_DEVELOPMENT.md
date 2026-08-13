# Transport development

AnyProtocol transport contracts are capability-oriented. `IMessagingProtocol`
contains only shared lifecycle and capability metadata; an implementation opts
into each operation it actually supports.

## API boundary classification

- Application API: `LinkBuilder`, contract attributes, client/event abstractions, fault types,
  payload policy options, and `ILargePayloadStore`.
- Provider SPI: `IMessagingProtocol`, operation capability interfaces, `TransportEnvelope`,
  `TransportSemantics`, codecs, and subscription contracts.
- Hosting SPI: `RuntimePlan`, `MessageDispatcher`, `TransportRegistry`,
  `OutboundOperationExecutor`, and `OutboundOperationLifetime`. ASP.NET Core, MCP, and Microsoft DI
  adapters compose these types; application code normally does not.
- Implementation detail: dispatch path components, route tables, subscription ownership, and
  `RuntimeLifetimeOwner` remain internal. Cross-assembly framework access uses explicit friend
  assemblies instead of public visibility.

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
    public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.None,
        Durability = TransportDurability.Volatile,
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

Capability interfaces are the source of truth for callable operations. The link
validator derives native request/reply and streaming support from
`IRequestReplyTransport` and `IStreamingTransport`. `TransportCapabilities`
remains for wire and broker properties that are not represented by an operation
interface. For emulated request/reply or streaming, the transport must expose
both send and subscription contracts. Missing contracts fail during
`LinkBuilder.Build`, before the bus starts.

`ISubscriptionTransport.SubscribeAsync` returns an `ITransportSubscription`.
Implement `StopAcceptingAsync` as the transport-specific quiesce operation and
keep it separate from `DisposeAsync`: the bus uses the former during the
`Draining` phase and the latter only after the bounded drain/forced-cancellation
window. Document whether quiescing preserves, requeues, or cancels an in-flight
callback. New callbacks must not be admitted after quiesce begins.
If a callback races with the shared admission gate and throws
`MessageAdmissionRejectedException`, durable transports must requeue or leave the
delivery uncommitted; it is a shutdown signal, not a handler fault or dead-letter case.
