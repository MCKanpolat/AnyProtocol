# RabbitMQ transport

`AnyProtocol.Protocol.RabbitMq` provides durable topic routing, publisher-confirmed sends, fan-out subscriptions, competing consumer groups, manual acknowledgements, bounded confirmed retries, dead-lettering, readiness, automatic connection/topology recovery, and graceful shutdown.

## Install and run locally

```shell
dotnet add package AnyProtocol.Protocol.RabbitMq
docker compose -f samples/docker-compose.yml up -d rabbitmq
```

The development broker listens on AMQP port `5672`; its management UI is at `http://localhost:15672`. The Compose credentials are development-only.

## Register

Read the URI from injected configuration and never log it:

```csharp
var uri = configuration["AnyProtocol:RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMQ URI is required.");

link.AddRabbitMq(new RabbitMqProtocolOptions
{
    ConnectionUri = new Uri(uri),
    ExchangeName = "orders",
    PrefetchCount = 32,
    MaxDeliveryAttempts = 5
});
```

The default key is `ProtocolKey.RabbitMq`; overloads accept another `ProtocolKey` or string for multiple isolated instances.

## Topology

The transport declares one durable topic exchange named by `ExchangeName`. A channel becomes a routing key, optionally prefixed by `RoutingKeyPrefix`.

- `ConsumerGroup = null` creates a server-named exclusive, auto-delete queue. Every such subscription receives a copy (fan-out).
- A named group creates a durable queue `{exchange}.{routing-key}.{group}`. Subscribers using that group compete for each message.
- When dead-lettering is enabled, `{exchange}{DeadLetterSuffix}` is a durable topic exchange and queue bound with `#`.

AMQP entity names are limited to 255 UTF-8 bytes. Use stable group names for durable workers and unique exchange/prefix values to isolate environments.

## Delivery lifecycle

Sends use persistent messages, mandatory routing, and publisher confirmations. `SendAsync` succeeds only after RabbitMQ accepts and routes the publish; unroutable messages and confirm timeouts fail the call.

Consumers use manual acknowledgements. A successful handler is acknowledged after completion. A cancellation is negatively acknowledged and requeued. For other failures:

1. `ShouldRetryHandlerException` classifies the exception.
2. A retryable delivery below `MaxDeliveryAttempts` is republished with an incremented `cl-delivery-attempt` header.
3. The original delivery is acknowledged only after the retry publish is confirmed. A failed republish requeues the original.
4. A permanent or exhausted delivery is confirmed to the dead-letter exchange, then the original is acknowledged.

Dead-letter metadata includes the source channel, exception type, generic `Handler execution failed.` text, and the delivery attempt. It excludes payload text, stack traces, exception messages, and credentials. Protect the DLQ because the original body and application headers are preserved.

## Options

| `RabbitMqProtocolOptions` property | Default |
|---|---|
| `ConnectionUri` | Required absolute `amqp`/`amqps` URI |
| `ClientProvidedName` | `anyprotocol-{random Guid:N}` |
| `ExchangeName` | `anyprotocol` |
| `RoutingKeyPrefix` | `null` |
| `PrefetchCount` | `32` |
| `MaxDeliveryAttempts` | `5` total deliveries |
| `ShouldRetryHandlerException` | Retry except `OperationCanceledException` |
| `EnableDeadLetter` | `true` |
| `DeadLetterSuffix` | `.dead-letter` |
| `ConfirmTimeout` | 10 seconds |
| `ReadinessTimeout` | 2 seconds |
| `NetworkRecoveryInterval` | 5 seconds |
| `ShutdownTimeout` | 10 seconds |

`SubscriptionOptions.MaxConcurrency` controls asynchronous handlers per subscription. `PrefetchCount` bounds unacknowledged messages on its channel. Start with prefetch near handler concurrency and tune from observed latency, broker memory, and handler duration.

## TLS, identity, and permissions

Use `amqps` in production and inject the URI from a secret provider. Give the application a dedicated virtual host/user with configure/write/read permissions limited to its exchange and queue patterns. RabbitMQ TLS validation, client certificates, authentication, policies, quotas, and network controls remain deployment responsibilities.

Avoid credentials in client names, exchange names, routing keys, group names, exception messages, or logs. Do not expose the management port publicly.

## Readiness, recovery, and shutdown

`RabbitMqMessagingProtocol` implements `ITransportReadiness`. The probe is ready only when the connection, channel, and exchange are available; caller cancellation propagates, while broker failures return a sanitized `NotReady` result. The client enables automatic connection and topology recovery using `NetworkRecoveryInterval`.

On shutdown, AnyProtocol cancels and disposes subscriptions before closing the publisher channel and connection. Active handlers receive cancellation and disposal is bounded by `ShutdownTimeout`. Configure the host and orchestrator termination grace period above that value plus application cleanup time.

## Troubleshooting

- **Unroutable publish:** ensure a queue is bound for the channel before sending, or start the consumer first.
- **Repeated delivery:** expected under at-least-once semantics; make handlers idempotent and add application deduplication where required.
- **No parallelism:** increase `SubscriptionOptions.MaxConcurrency`, prefetch, and worker replicas; one queue still delivers each message to one consumer.
- **Messages in DLQ:** inspect `cl-dead-letter-source`, `cl-dead-letter-error-type`, and `cl-delivery-attempt`; keep body access restricted.
- **Not ready:** verify URI reachability, TLS trust, virtual-host permissions, exchange policy, and broker health without printing the URI.
- **Slow confirms:** inspect broker disk alarms, quorum/policy configuration, network latency, and publisher throughput. The current publisher channel intentionally serializes confirmed sends.

RabbitMQ's AnyProtocol profile is at least once, durable, and ordered per channel only with a single active consumer/handler. Concurrent consumers can complete out of order. Request/reply and streaming are framework emulations, not native capability flags.
