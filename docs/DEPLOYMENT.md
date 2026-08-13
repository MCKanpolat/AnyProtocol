# Deployment

## Local infrastructure verification

`dotnet test AnyProtocol.slnx` starts Kafka, RabbitMQ, and Redis Testcontainers automatically when
external endpoints are not configured. For a strict local release check, require container startup:

```powershell
$env:ANYPROTOCOL_REQUIRE_BROKER_TESTS = "true"
dotnet test AnyProtocol.slnx
```

An unavailable required container fails its fixture with an infrastructure-specific message. Without
strict mode, unavailable infrastructure is reported as skipped rather than being mixed with passing
unit tests.

## ASP.NET Core hosting

REST servers require all three calls:

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddRestServer("rest")
    .AddServer<IOrders, Orders>(server => server.UseTransport("rest")));
builder.Services.AddAnyProtocolRest();

var app = builder.Build();
app.MapAnyProtocol("/api");
```

gRPC replaces those transport-specific calls with `AddGrpcServer`, `AddAnyProtocolGrpc`, and `MapAnyProtocolGrpc`. Mapping marks the server transport ready; omitting it leaves readiness unhealthy.

## Health probes

```csharp
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

builder.Services.AddHealthChecks().AddAnyProtocolHealthChecks();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration =>
        registration.Tags.Contains(AnyProtocolHealthChecks.LiveTag)
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration =>
        registration.Tags.Contains(AnyProtocolHealthChecks.ReadyTag)
});
```

Liveness is unhealthy only after the bus is disposed. Readiness requires `AnyProtocolBusState.Started` and rejects any transport reporting `NotReady`; transports without a readiness contributor are recorded as `not-assessed`.

Kubernetes example:

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: http
readinessProbe:
  httpGet:
    path: /health/ready
    port: http
startupProbe:
  httpGet:
    path: /health/ready
    port: http
  failureThreshold: 30
  periodSeconds: 2
```

Set probe timings from measured broker/channel startup behavior.

## Graceful shutdown

The Microsoft DI package registers an `IHostedService`. On host shutdown it:

1. changes bus state to `Draining` and closes the shared admission gate;
2. stops accepting new subscription callbacks;
3. waits for admitted handlers up to `ShutdownOptions.DrainTimeout`;
4. cancels remaining work and waits up to `ShutdownOptions.ForcedCancellationTimeout`;
5. disposes subscriptions and transports, then changes state to `Stopped`.

The default drain timeout is 30 seconds and the forced-cancellation timeout is 5 seconds. Configure ASP.NET Core host shutdown timeout and the Kubernetes termination grace period to exceed those bounds plus transport flush allowance. Native REST, gRPC, and MCP endpoints should share the registered `IRequestAdmission` singleton; requests rejected after drain begins must use their transport-specific retryable response (`503`/`Unavailable`/MCP retryable error).

## Kafka networking and scaling

- Configure `BootstrapServers` with broker addresses reachable from the workload network, not container-local `localhost`.
- Disable `AutoCreateTopics` where operators manage topics, then pre-create request, reply, event, and dead-letter topics with appropriate partitions, replication, retention, and ACLs.
- The default replication factor is one and is not a production recommendation.
- Scale workers with a shared event/contract consumer group. Kafka assigns partitions across consumers; replicas beyond partition count do not increase parallel consumption.
- Scale publishers independently. Producer idempotence reduces duplicate production but does not make handlers exactly once.
- Readiness performs a synchronous metadata lookup with a fixed two-second timeout. Broker loss makes the pod unready; send/consume failures surface as Kafka/IO errors rather than success-shaped fallbacks.

## ZeroMQ networking and scaling

- The server binds `RouterEndpoint` and `PublisherEndpoint`; clients connect to both. Expose two TCP ports and advertise addresses clients can resolve.
- One server instance must own a given bind endpoint. Horizontal server scaling therefore needs distinct endpoints plus external discovery/load distribution; AnyProtocol does not provide it.
- Publisher/subscriber delivery is volatile. Subscribers joining late miss earlier messages.
- Client readiness is `Unknown`; use a separate application-level dependency check if peer reachability must gate traffic.
- The outbound queue is bounded by `HighWatermark` and waits when full. Socket-loop failure faults pending writes and future availability.

## RabbitMQ networking and scaling

- Workloads connect to AMQP `5672` or the deployment's TLS listener; management port `15672` is operational access, not an application dependency.
- Use `amqps`, a dedicated virtual host/user, least-privilege configure/write/read patterns, and secret injection. Do not use Compose guest credentials outside local development.
- Scale a worker pool with the same named `ConsumerGroup`; use null groups only when every replica should receive a copy.
- Tune `PrefetchCount`, `MaxConcurrency`, and replica count together. Concurrent consumers increase throughput but remove completion ordering.
- Readiness checks the connection/channel/exchange within `ReadinessTimeout`. Automatic connection and topology recovery retries at `NetworkRecoveryInterval`.
- Set host termination grace above `ShutdownTimeout`. Subscriptions are cancelled before the publisher channel and connection close.
- Monitor queue depth, unacknowledged messages, redeliveries, dead-letter depth, publisher confirms, disk/memory alarms, and connection churn in RabbitMQ.

## Operational failure behavior

- Configuration and endpoint-shape errors fail startup.
- Partial subscription startup is rolled back and reported as an aggregate failure.
- REST non-success responses become `FaultMessage` envelopes; direct send maps validation faults to `AnyProtocolValidationException` and other faults to `AnyProtocolFaultException`.
- gRPC status failures become `IOException`, cancellation becomes `OperationCanceledException`, and stream deadline expiry becomes `TimeoutException`.
- Kafka handler faults for events are sent to the configured dead-letter topic when enabled; disabling dead-lettering makes that path fail.
- ZeroMQ socket-loop, decode, handler, pending-send, and forced-shutdown failures use `ILogWriterFactory` with stable event IDs. The default writer is a no-op; register `AnyProtocol.Logging.Microsoft` or another adapter for application logs.
