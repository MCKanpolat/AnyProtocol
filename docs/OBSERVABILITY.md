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
| `anyprotocol.messaging.retries` | `Counter<long>` | `{retry}` |
| `anyprotocol.messaging.dead_letters` | `Counter<long>` | `{message}` |

## Tags and cardinality

Every measurement uses:

- `anyprotocol.contract`
- `anyprotocol.method`
- `anyprotocol.transport`
- `anyprotocol.operation`: `request`, `event`, `stream`, or `unknown`

Completed operations and handler measurements also use `anyprotocol.outcome`: `success`, `fault`, `timeout`, `cancelled`, or `unknown`.

Contract, method, and transport identifiers are limited to 128 characters. ASCII letters, digits, `.`, `_`, `-`, and `+` are preserved; other characters become `_`; missing values become `unknown`. Do not place tenant IDs, message IDs, user IDs, partition keys, channels containing user data, or other unbounded values into these identifiers.

Activities add the same tags. Errors set `exception.type`, `anyprotocol.outcome`, and `ActivityStatusCode.Error`; exception messages, stack traces, and payloads are not recorded by the built-in tracing filter.

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

| EventId | Constant | Emitted by `LoggingFilter` |
|---:|---|---:|
| 1000 | `OperationStarted` | Yes, Debug |
| 1001 | `OperationCompleted` | Yes, Debug |
| 1100 | `RetryScheduled` | No |
| 1200 | `OperationTimedOut` | Yes, Error |
| 1201 | `OperationCancelled` | Yes, Error |
| 1300 | `OperationFaulted` | Yes, Error |
| 1400 | `DeadLettered` | No |
| 1500 | `ReadinessFailed` | No |

The filter logs message type, contract, method, and exception type only. It does not log body, channel, message ID, token, exception message, or stack trace.

`AnyProtocolLogRedactor` provides `[REDACTED]` for credential-like names and `[PAYLOAD OMITTED]` unless callers explicitly pass `includePayload: true`. The core logging filter never opts into payload logging. Application logs and third-party HTTP/gRPC/Kafka instrumentation need their own redaction policy.
