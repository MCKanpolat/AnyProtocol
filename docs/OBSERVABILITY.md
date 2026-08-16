# Observability

AnyProtocol emits `System.Diagnostics` traces and metrics without depending on the OpenTelemetry SDK. Exporters belong in the host application.

## Sources

| Signal | Name | Version |
|---|---|---|
| `ActivitySource` | `AnyProtocol.Core` | `1.0.0` |
| `Meter` | `AnyProtocol.Core` | `1.0.0` |

Activities are named `anyprotocol {operation}` and use `Producer` for outbound and `Consumer` for inbound pipeline work. W3C `traceparent` and `tracestate` headers are injected outbound and extracted inbound.

## Instruments

| Instrument | Type | Unit |
|---|---|---|
| `anyprotocol.messaging.operations.started` | `Counter<long>` | `{operation}` |
| `anyprotocol.messaging.operations.completed` | `Counter<long>` | `{operation}` |
| `anyprotocol.messaging.operation.duration` | `Histogram<double>` | `s` |
| `anyprotocol.messaging.handler.duration` | `Histogram<double>` | `s` |
| `anyprotocol.messaging.requests.active` | `UpDownCounter<long>` | `{request}` |
| `anyprotocol.messaging.streams.active` | `UpDownCounter<long>` | `{stream}` |
| `anyprotocol.messaging.shutdown.active` | `UpDownCounter<long>` | `{work}` |
| `anyprotocol.messaging.shutdown.forced_cancellations` | `Counter<long>` | `{work}` |
| `anyprotocol.transport.failures` | `Counter<long>` | `{failure}` |
| `anyprotocol.messaging.retries` | `Counter<long>` | `{retry}` |
| `anyprotocol.messaging.dead_letters` | `Counter<long>` | `{message}` |
| `anyprotocol.payload.messages` | `Counter<long>` | `{message}` |
| `anyprotocol.payload.bytes` | `Counter<long>` | `By` |
| `anyprotocol.payload.store.duration` | `Histogram<double>` | `s` |
| `anyprotocol.payload.failures` | `Counter<long>` | `{failure}` |
| `anyprotocol.payload.age` | `Histogram<double>` | `s` |

## Tags and cardinality

Operation, handler, active request/stream, retry, and dead-letter measurements use:

- `anyprotocol.contract`
- `anyprotocol.method`
- `anyprotocol.transport`
- `anyprotocol.operation`: `request`, `event`, `stream`, or `unknown`

Completed operations, handler, and dead-letter measurements also use `anyprotocol.outcome`:
`success`, `fault`, `timeout`, `cancelled`, or `unknown`.

Shutdown measurements use only `anyprotocol.work=admitted`. Transport failures use
`anyprotocol.transport` and the supplied `anyprotocol.failure` category; callers should keep
failure categories stable and bounded. They do not carry contract or method identifiers.

Contract, method, and transport identifiers are limited to 128 characters. ASCII letters, digits, `.`, `_`, `-`, and `+` are preserved; other characters become `_`; missing values become `unknown`. Do not place tenant IDs, message IDs, user IDs, partition keys, channels containing user data, or other unbounded values into these identifiers.

Activities add the same tags. Errors set `exception.type`, `anyprotocol.outcome`, and `ActivityStatusCode.Error`; exception messages, stack traces, and payloads are not recorded by the built-in tracing filter.

Payload measurements use bounded `anyprotocol.payload.mode` and `direction` for message counts;
`anyprotocol.payload.operation` and `store` for byte/store duration; and `failure` plus
`operation` for failures. Payload age currently has no tags. Store names are normalized and
capped; opaque keys and message IDs are never tags. Stored activities add mode, logical store,
and byte count.

## OpenTelemetry hookup

Add the OpenTelemetry SDK and the exporters/instrumentation required by the host, then select the stable source names:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing => tracing
        .AddSource(AnyProtocolDiagnostics.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(AnyProtocolDiagnostics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
```

Attach OTLP, Prometheus, Azure Monitor, console, or another exporter in the application. Core does not choose an exporter, endpoint, sampling policy, histogram view, or resource attributes.

## Health checks

`AddAnyProtocolHealthChecks` registers:

| Name | Tag | Meaning |
|---|---|---|
| `anyprotocol_liveness` | `live` | Healthy unless the bus is disposed |
| `anyprotocol_readiness` | `ready` | Bus started and no transport reports `NotReady` |

Use the exact `/health/live` and `/health/ready` mapping in [deployment](DEPLOYMENT.md#health-probes).

RabbitMQ contributes `Ready` only while its connection, channel, and exchange are reachable, and returns sanitized `NotReady` descriptions after disposal or broker failure. AnyProtocol owns this availability state and framework operation metrics. RabbitMQ client/broker exporters own connection recovery, confirms, queue depth, unacknowledged deliveries, redeliveries, consumer utilization, dead-letter depth, and resource alarms; correlate them in the host without placing credentials or message bodies in labels.

## Logging, EventIds, and redaction

`LoggingFilter` writes category `AnyProtocol.Pipeline`.

| EventId | Constant | Source / level |
|---:|---|---|
| 1000 | `OperationStarted` | `LoggingFilter` / Debug |
| 1001 | `OperationCompleted` | `LoggingFilter` / Debug |
| 1100 | `RetryScheduled` | Reserved; not emitted by the built-in logger |
| 1200 | `OperationTimedOut` | `LoggingFilter` / Error |
| 1201 | `OperationCancelled` | `LoggingFilter` / Error |
| 1300 | `OperationFaulted` | `LoggingFilter` / Error |
| 1400 | `DeadLettered` | Reserved; not emitted by the built-in logger |
| 1500 | `ReadinessFailed` | Reserved; not emitted by the built-in logger |
| 1600 | `UnhandledMessageError` | `FaultMessageSender` / Error |
| 1601 | `FaultDeliveryFailed` | `FaultMessageSender` / Error |
| 1602 | `ErrorHandlerFailed` | `FaultMessageSender` / Error |
| 1603 | `RoutingFailed` | `InboundMessageRouter` / Warning or Error |
| 1604 | `StreamDeliveryFailed` | `InboundMessageRouter` / Error |
| 1605 | `ForcedShutdownCancellation` | `AnyProtocolBus` / Warning |
| 1700 | `ZeroMqSocketLoopFailed` | `ZeroMqMessagingProtocol` / Error |
| 1701 | `ZeroMqDecodeFailed` | `ZeroMqMessagingProtocol` / Error |
| 1702 | `ZeroMqHandlerFailed` | `ZeroMqMessagingProtocol` / Error |
| 1703 | `ZeroMqPendingOutboundFailed` | `ZeroMqMessagingProtocol` / Error |

The filter logs message type, contract, method, and exception type only. It does not log body, channel, message ID, token, exception message, or stack trace.

The core default is a no-op `ILogWriterFactory`. To bridge the abstraction to
`Microsoft.Extensions.Logging`, install `AnyProtocol.Logging.Microsoft` and register its
`MicrosoftLogWriterFactory` with the host's `ILoggerFactory`.

`AnyProtocolLogRedactor` provides `[REDACTED]` for credential-like names and `[PAYLOAD OMITTED]` unless callers explicitly pass `includePayload: true`. The core logging filter never opts into payload logging. Application logs and third-party HTTP/gRPC/Kafka instrumentation need their own redaction policy.
