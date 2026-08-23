# ZeroMQ protocol

`AnyProtocol.Protocol.ZeroMq` provides brokerless socket-based messaging. A server binds the router and publisher endpoints; clients connect to those endpoints.

## Install

```shell
dotnet add package AnyProtocol.Protocol.ZeroMq
```

## Server

```csharp
link.AddTransport(ProtocolKey.ZeroMq, new ZeroMqMessagingProtocol(new ZeroMqProtocolOptions
{
    Role = ZeroMqRole.Server,
    RouterEndpoint = "tcp://*:5555",
    PublisherEndpoint = "tcp://*:5556"
}));
link.AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.ZeroMq));
```

## Client

```csharp
link.AddTransport(ProtocolKey.ZeroMq, new ZeroMqMessagingProtocol(new ZeroMqProtocolOptions
{
    Role = ZeroMqRole.Client,
    RouterEndpoint = "tcp://orders.example:5555",
    PublisherEndpoint = "tcp://orders.example:5556"
}));
link.AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.ZeroMq));
```

## Options and semantics

| `ZeroMqProtocolOptions` property | Default |
|---|---|
| `Role` | Required (`Client` or `Server`) |
| `RouterEndpoint` | Required |
| `PublisherEndpoint` | Required |
| `ClientIdentity` | Random Guid when omitted |
| `HighWatermark` | `1,000` |
| `SubscriptionQueueCapacity` | `1,000` |
| `PollInterval` | 2 milliseconds |

- At-most-once delivery, volatile storage, and per-channel ordering.
- Publish/subscribe and competing consumer groups are supported.
- Request/reply and streaming are AnyProtocol emulations.
- There is no broker persistence, delivery acknowledgement, built-in reconnect state store, or non-destructive client readiness probe.
- Local subscription queues are bounded and apply backpressure to socket processing rather than
  silently dropping messages. Size `SubscriptionQueueCapacity` with handler latency and payload
  size in mind; ZeroMQ remains an at-most-once transport.

See [Deployment](DEPLOYMENT.md) for endpoint and scaling guidance.
