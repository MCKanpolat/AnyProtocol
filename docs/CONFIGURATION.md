# Configuration reference

AnyProtocol keeps contract and implementation registration code-first. Named client and server registrations can overlay their protocol selections from `IConfiguration`, including environment-variable providers.

```json
{
  "AnyProtocol": {
    "Clients": {
      "OrdersClient": { "Protocol": "Grpc" }
    },
    "Servers": {
      "OrdersServer": { "Protocols": ["Rest", "Grpc", "Mcp"] }
    }
  }
}
```

```csharp
builder.Services.AddAnyProtocol(
    builder.Configuration.GetSection("AnyProtocol"),
    link => link
        .UseSerializer(new TextJsonMessageSerializer())
        .AddRestServer(ProtocolKey.Rest)
        .AddGrpcServer(ProtocolKey.Grpc)
        .AddClient<IOrders>("OrdersClient")
        .AddServer<IOrders, Orders>("OrdersServer"));
```

The configuration section overrides only protocol selections; serializers, transports, contract types, implementations, filters, timeouts, and retries stay in code. Missing entries preserve code defaults. Unknown registration keys, missing client protocol values, empty server protocol arrays, unknown protocols, and duplicate protocols fail atomically during registration.

## Core builder defaults

| API | Default |
|---|---|
| `ClientOptionsBuilder.UseProtocol` | `ProtocolKey.Default` |
| `ServerOptionsBuilder.UseProtocols` | `[ProtocolKey.Default]` |
| `EventOptionsBuilder.UseProtocol` | `ProtocolKey.Default` |
| Client timeout | 30 seconds |
| Client maximum attempts | 1 |
| Event channel | `anyprotocol.event.{event-type-in-kebab-case}` |
| Event consumer group | `null` |
| Required ordering | `TransportOrdering.None` |
| `SubscriptionOptions.MaxConcurrency` | 1 |
| `SubscriptionOptions.ConsumerGroup` | `null` |

`UseSerializer` and every referenced transport are required. Protocol keys are case-insensitive and duplicates fail configuration. String-based `UseTransport` and `AddTransport` overloads remain available for compatibility.

`ProtocolKey` provides `Default`, `Rest`, `Grpc`, `Mcp`, `Kafka`, `RabbitMq`, `ZeroMq`, and `InMemory`. Use `ProtocolKey.Create(name)` for custom protocols or named instances. One contract cannot select the same native server transport type through two keys because that would create ambiguous REST or gRPC routes.

Clients select one protocol. Servers select one or more protocols in a single logical registration:

```csharp
link.AddServer<IOrders, Orders>(server => server.UseProtocols(
    ProtocolKey.Rest,
    ProtocolKey.Grpc,
    ProtocolKey.Mcp));
```

Validation is atomic: if any selected protocol is unknown or lacks a capability required by any contract method, configuration build fails and no partial endpoint set is exposed. If MCP is selected, host startup also requires `AddAnyProtocolMcp()` plus `MapAnyProtocolMcp()`, or `AddAnyProtocolMcpStdio()`; an incomplete exposure stops the host.

## Transport options

### InMemory

`new InMemoryMessagingProtocol()` uses `InMemoryProtocolOptions` with:

| Option | Default |
|---|---|
| `DeliveryDelay` | `TimeSpan.Zero` |
| `FaultInjector` | `null` |

These options are intended for process-local execution and deterministic fault/delay testing.

### REST

`RestMessagingProtocol` accepts either `HttpClient` plus an `IMessageSerializer`, or `IHttpClientFactory` plus a serializer and `clientName`; both overloads accept `routePrefix`. The prefix defaults to `/anyprotocol`. `AddRestServer("rest")` has no additional options; `MapAnyProtocol("/anyprotocol")` supplies the server prefix.

Channels cannot contain `{`, `}`, `?`, `#`, empty path segments, or whitespace-only segments.

### gRPC

`GrpcMessagingProtocol(GrpcChannel channel, bool disposeChannel = false)` or `GrpcMessagingProtocol(CallInvoker)` configures the client. A `CallInvoker` cannot report channel connectivity, so readiness is `Unknown`. `AddGrpcServer("grpc")` and `MapAnyProtocolGrpc()` have no transport options.

### Kafka

| `KafkaProtocolOptions` property | Default |
|---|---|
| `BootstrapServers` | Required |
| `ClientId` | `anyprotocol-{random Guid:N}` |
| `TopicPrefix` | `null` |
| `AutoCreateTopics` | `true` |
| `TopicPartitions` | 3 |
| `TopicReplicationFactor` | 1 |
| `ReplyTopicRetention` | 5 minutes |
| `DeadLetterSuffix` | `.dead-letter` |
| `EnableDeadLetter` | `true` |
| `SubscriptionStartupTimeout` | 30 seconds |
| `ConsumerPollInterval` | 100 milliseconds |
| `ProducerFlushTimeout` | 10 seconds |
| `ProducerConfig` / `ConsumerConfig` | Empty ordinal dictionaries |

The built producer starts with idempotence enabled and `Acks.All`; `ProducerConfig` overrides are applied afterward. Consumers disable auto-commit and auto-offset-store. Topic names are limited to 249 ASCII letters/digits plus `.`, `_`, and `-`.

### ZeroMQ

| `ZeroMqProtocolOptions` property | Default |
|---|---|
| `Role` | Required (`Client` or `Server`) |
| `RouterEndpoint` | Required |
| `PublisherEndpoint` | Required |
| `ClientIdentity` | `null` (a random Guid is used) |
| `HighWatermark` | 1,000 |
| `PollInterval` | 2 milliseconds |

The high watermark must be positive and poll interval non-negative.

### RabbitMQ

| `RabbitMqProtocolOptions` property | Default |
|---|---|
| `ConnectionUri` | Required absolute `amqp`/`amqps` URI |
| `ClientProvidedName` | `anyprotocol-{random Guid:N}` |
| `ExchangeName` | `anyprotocol` |
| `RoutingKeyPrefix` | `null` |
| `PrefetchCount` | 32 |
| `MaxDeliveryAttempts` | 5 |
| `ShouldRetryHandlerException` | Retry except cancellation |
| `EnableDeadLetter` | `true` |
| `DeadLetterSuffix` | `.dead-letter` |
| `ConfirmTimeout` | 10 seconds |
| `ReadinessTimeout` | 2 seconds |
| `NetworkRecoveryInterval` | 5 seconds |
| `ShutdownTimeout` | 10 seconds |

```csharp
var uri = builder.Configuration["ANYPROTOCOL_RABBITMQ_URI"]
    ?? throw new InvalidOperationException("RabbitMQ URI is required.");

link.AddRabbitMq(new RabbitMqProtocolOptions
{
    ConnectionUri = new Uri(uri),
    ExchangeName = builder.Configuration["ANYPROTOCOL_RABBITMQ_EXCHANGE"] ?? "orders"
});
```

The application owns these example environment names. Never print the URI because it may contain credentials.

## Serializers

- `TextJsonMessageSerializer()` uses `JsonSerializerOptions.Default`.
- `TextJsonMessageSerializer(JsonSerializerOptions)` uses the supplied options.
- `TextJsonMessageSerializer(JsonSerializerContext)` is the Native AOT-safe form.
- `MessagePackMessageSerializer` uses `ContractlessStandardResolver.Options`.

All serialization failures are wrapped in `SerializationFailedException`. Producer and consumer processes must agree on serializer and message shape.

## Timeout, retry, and idempotency

`WithTimeout(TimeSpan)` sets the client request timeout and rejects non-positive values. `WithRetry(int maxAttempts)` sets total attempts, not retry count, and rejects values below one.

For explicit pipeline control:

```csharp
link.AddClientFilter(new TimeoutFilter(TimeSpan.FromSeconds(10)));
link.AddClientFilter(new RetryFilter(new RetryOptions
{
    MaxAttempts = 3,
    InitialDelay = TimeSpan.FromMilliseconds(100),
    MaxDelay = TimeSpan.FromSeconds(5),
    JitterFactor = 0.2,
    MaxTotalTime = TimeSpan.FromSeconds(20)
}));
```

`RetryOptions` defaults are one attempt, 100 ms initial delay, 5 s maximum delay, 0.2 jitter, and no total-time limit. The filter retries only methods marked `[Idempotent]`, and only `TimeoutException`, `IOException`, or `AnyProtocolFaultException` whose fault has `Retryable = true`. The attribute does not store idempotency keys or deduplicate delivery.

## Lifecycle

`AddAnyProtocol` registers `AnyProtocolHostedService`. Host startup calls `IAnyProtocolBus.StartAsync`; shutdown calls `StopAsync`. Startup subscribes event and emulated-server routes before changing the state to `Started`. Failure rolls back created subscriptions and throws an `AggregateException`. Shutdown cancels the bus run token and disposes subscriptions; disposal additionally disposes transports.

## Environment example

Read configuration explicitly and construct typed options:

```csharp
var kafka = new KafkaProtocolOptions
{
    BootstrapServers = builder.Configuration["ANYPROTOCOL_KAFKA_BOOTSTRAP_SERVERS"]
        ?? throw new InvalidOperationException("Kafka bootstrap servers are required."),
    TopicPrefix = builder.Configuration["ANYPROTOCOL_KAFKA_TOPIC_PREFIX"],
    AutoCreateTopics = builder.Configuration.GetValue(
        "ANYPROTOCOL_KAFKA_AUTO_CREATE_TOPICS",
        false)
};

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddKafka(kafka));
```

Example process values:

```shell
ANYPROTOCOL_KAFKA_BOOTSTRAP_SERVERS=kafka-0:9092,kafka-1:9092
ANYPROTOCOL_KAFKA_TOPIC_PREFIX=orders
ANYPROTOCOL_KAFKA_AUTO_CREATE_TOPICS=false
```

These names are examples owned by the application, not reserved framework keys.

## Startup validation errors

Configuration fails before the service provider is returned when:

- no serializer is configured;
- a contract or event references an unknown transport;
- a server, request/reply, stream, consumer group, partition key, or required ordering is unsupported by the selected transport;
- an event has multiple partition keys, or its key is unreadable/indexed;
- per-partition ordering is required without a partition key on every operation;
- the same server route is registered more than once on one transport;
- a transport name is duplicated;
- timeout, retry, concurrency, Kafka, REST route, or ZeroMQ option validation fails;
- Native AOT generated registration or serializer metadata is incomplete.
