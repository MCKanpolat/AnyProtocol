# InMemory protocol

`AnyProtocol.Protocol.InMemory` is the zero-dependency, process-local transport. It is the best starting point for samples, unit tests, contract validation, and deterministic fault or latency tests.

## Install

```shell
dotnet add package AnyProtocol.Protocol.InMemory
```

## Register

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddTransport(ProtocolKey.InMemory, new InMemoryMessagingProtocol())
    .AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.InMemory))
    .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.InMemory)));
```

Use the same transport instance for the client and server when both ends run in one process. `AddAnyProtocol` owns its lifecycle and disposes the transport with the bus.

## Options

| Option | Default |
|---|---|
| `DeliveryDelay` | `TimeSpan.Zero` |
| `FaultInjector` | `null` |

`DeliveryDelay` is useful for timeout and cancellation tests. `FaultInjector` can fail a send before it reaches subscribers.

## Semantics

- At-most-once delivery, volatile storage, and per-channel ordering.
- Publish/subscribe and competing consumer groups are supported.
- Messages are copied before delivery, so handlers cannot mutate the sender's envelope.
- Request/reply and streaming use AnyProtocol's framework emulation.
- No broker, network listener, persistence, or cross-process delivery is provided.

For the complete capability matrix, see [Transport semantics](TRANSPORT_SEMANTICS.md).
