# Transport semantics

This document mirrors the implemented `TransportSemantics` profiles. A `true` capability describes framework-observable behavior; it is not a claim about application-level exactly-once processing, remote cancellation, or external security.

## Authoritative profiles

| Transport | Delivery | Ordering | Durability | Pub/sub | Competing | Native req/reply | Native stream | Partitioning | Backpressure | Cancellation |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| InMemory | At most once | Per channel | Volatile | Yes | Yes | No | No | No | Yes | Yes |
| REST client/server | At most once | None | Volatile | No | No | Yes | No | No | Yes | Yes |
| gRPC client/server | At most once | None | Volatile | No | No | Yes | Yes | No | Yes | Yes |
| Kafka | At least once | Per partition | Durable | Yes | Yes | No | No | Yes | Yes | Yes |
| RabbitMQ | At least once | Per channel | Durable | Yes | Yes | No | No | No | Yes | Yes |
| ZeroMQ | At most once | Per channel | Volatile | Yes | Yes | No | No | No | Yes | Yes |

All unspecified `TransportSemantics` Boolean properties are `false`.

## Delivery, ordering, and durability

- **At most once** means AnyProtocol does not redeliver after a failed accepted send. Loss remains possible on process, connection, or peer failure.
- **Kafka at least once** uses producer idempotence and `Acks.All`, disables consumer auto-commit/offset-store, and stores offsets after successful handling. Handler re-execution remains possible.
- **RabbitMQ at least once** uses persistent mandatory confirmed publishing, manual acknowledgements, confirmed retry republishing, and a durable dead-letter exchange. Handler re-execution remains possible.
- **Per channel** applies to the transport's send path; concurrent handlers (`MaxConcurrency > 1`) can complete out of order.
- **Per partition** is Kafka's boundary. `[PartitionKey]` is supported only by Kafka and is required on every operation when `RequireOrdering(PerPartition)` is configured.
- Volatile transports do not survive process restart. Kafka durability still depends on broker replication, retention, acknowledgements, and topic configuration.

There is no exactly-once application guarantee.

## Fan-out and competing consumers

InMemory and ZeroMQ deliver ungrouped subscriptions to every subscriber. Subscribers with the same non-null `ConsumerGroup` compete, with one group member selected per message.

Kafka creates a unique group when `ConsumerGroup` is null, producing fan-out across subscriptions. An explicit shared group delegates competition and partition assignment to Kafka. `MaxConcurrency` defaults to one and creates multiple workers for a subscription.

RabbitMQ uses a server-named exclusive auto-delete queue for a null group, so each subscription receives a copy. A named group maps to one stable durable queue, so its consumers compete. Prefetch bounds unacknowledged delivery; `MaxConcurrency > 1` can complete messages out of order.

REST and gRPC server transports dispatch mapped requests and do not expose subscriptions, fan-out, or consumer groups.

## Request/reply

REST and gRPC implement `IRequestReplyTransport`; gRPC also implements `IStreamingTransport`. InMemory, Kafka, RabbitMQ, and ZeroMQ emulate request/reply using a unique `_anyprotocol.reply.*` subscription and correlation headers. A positive client timeout defaults to 30 seconds.

Kafka reply topics default to five-minute retention. The other emulated transports have no durable reply storage.

## Streaming and backpressure

gRPC uses native server streaming. InMemory, Kafka, and ZeroMQ emulate streams over publish/subscribe. The emulated `StreamEngine` uses a bounded channel of 32 envelopes by default with `BoundedChannelFullMode.Wait`, validates sequence numbers, and propagates stream completion/fault envelopes.

REST does not support streaming contracts. Backpressure means local async writes wait when bounded transport/stream buffers are full; it does not guarantee bounded memory in every internal or broker queue.

## Cancellation

All built-in profiles set `SupportsCancellation = true`: public async operations honor their cancellation token while sending, receiving, waiting, or dispatching. REST and gRPC pass cancellation to their client calls. Kafka and ZeroMQ cancellation cannot recall a message already accepted by a broker/socket. Emulated cancellation does not create a transport-level remote cancel message.

## Readiness

Readiness is separate from `TransportSemantics`:

| Transport | Readiness behavior |
|---|---|
| InMemory | Ready until disposed |
| REST server | Ready only after `MapAnyProtocol` marks endpoints mapped |
| REST client | Not assessed (does not implement `ITransportReadiness`) |
| gRPC server | Ready only after `MapAnyProtocolGrpc` marks the service mapped |
| gRPC client with `GrpcChannel` | Connects and checks `ConnectivityState.Ready` |
| gRPC client with `CallInvoker` | Unknown |
| Kafka | Ready when broker metadata returns at least one broker; fixed 2-second metadata timeout |
| RabbitMQ | Ready when the connection, channel, and durable exchange are available; bounded by `ReadinessTimeout` |
| ZeroMQ server | Ready while the socket loop runs and bound successfully |
| ZeroMQ client | Unknown because no non-destructive peer probe exists |

`Unknown` does not fail the aggregate AnyProtocol readiness check; `NotReady` does.

## Known limitations

- InMemory is process-local and intended for local execution/testing.
- REST has no subscriptions or streams.
- gRPC exposes unary and server-streaming dispatch, not publish/subscribe.
- Kafka topic auto-creation defaults are development-oriented; at-least-once delivery requires idempotent handlers.
- RabbitMQ ordering requires a single active consumer/handler per queue; retries and recovery require idempotent handlers.
- ZeroMQ has no broker persistence, delivery acknowledgements, built-in reconnect state store, or non-destructive client peer readiness.
- Consumer groups, retries, and `[Idempotent]` do not provide deduplication.
- Ordering requirements are exact matches: requesting `PerChannel` does not accept a `PerPartition` profile, or vice versa.
