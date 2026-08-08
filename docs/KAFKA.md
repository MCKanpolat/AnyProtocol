# Kafka protocol

`AnyProtocol.Protocol.Kafka` maps logical channels to Kafka topics and uses consumer groups for competing consumers. It is the durable, partition-aware choice for event streams and horizontally scaled workers.

## Install and run locally

```shell
dotnet add package AnyProtocol.Protocol.Kafka
docker compose -f samples/docker-compose.yml up -d
```

## Register

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddKafka(new KafkaProtocolOptions
    {
        BootstrapServers = "localhost:9092",
        TopicPrefix = "orders",
        AutoCreateTopics = false
    })
    .AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Kafka))
    .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.Kafka)));
```

Use broker addresses reachable from the workload network, not container-local `localhost`, in production.

## Options

| `KafkaProtocolOptions` property | Default |
|---|---|
| `BootstrapServers` | Required |
| `TopicPrefix` | `null` |
| `AutoCreateTopics` | `true` |
| `TopicPartitions` | `3` |
| `TopicReplicationFactor` | `1` |
| `ReplyTopicRetention` | 5 minutes |
| `EnableDeadLetter` | `true` |
| `ProducerFlushTimeout` | 10 seconds |

Producer idempotence and `Acks.All` are enabled by default. Consumer auto-commit and auto-offset-store are disabled; offsets are stored after successful handling.

## Semantics

- At-least-once delivery and durable topics.
- Ordering is per partition; use `[PartitionKey]` when partition ordering matters.
- Publish/subscribe, competing consumers, and backpressure are supported.
- Request/reply and streaming use `_anyprotocol.reply.*` topic emulation.
- Handler re-execution remains possible; make handlers idempotent.

See [Deployment](DEPLOYMENT.md) for broker networking and [Transport semantics](TRANSPORT_SEMANTICS.md) for the full guarantee matrix.
