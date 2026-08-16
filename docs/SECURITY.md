# Security

AnyProtocol supplies message metadata hooks; it does not establish identity, terminate TLS, store secrets, issue tokens, encrypt payloads, or create network policy.

Large-payload providers are optional application infrastructure. Their credentials never travel in
an envelope; see [large payload offload](LARGE_PAYLOADS.md#redis-sizing-and-security) for namespace,
TTL, integrity, memory, and ACL requirements.

## Tokens and permissions

Implement `IAuthTokenProvider` and add `AuthTokenFilter` to the client pipeline:

```csharp
var tokenProvider = new WorkloadTokenProvider();
builder.Services.AddSingleton<IAuthTokenProvider>(tokenProvider);

builder.Services.AddAnyProtocol(link => link
    // serializer, transport, and contract registrations
    .AddClientFilter(new AuthTokenFilter(tokenProvider)));
```

On outbound messages the filter calls `GetTokenAsync` and, for a non-empty result, writes `HeaderNames.AuthToken` (`cl-auth-token`).

Mark protected operations:

```csharp
[RequirePermission("orders.read")]
ValueTask<Order> GetAsync(GetOrder request, CancellationToken cancellationToken);
```

Add `AuthorizationFilter` to the server pipeline and register an `IAuthorizationProvider`. The filter passes the declared permission strings to `CheckPermissionsAsync`. Your provider is responsible for authenticating the request, locating the propagated credential/principal through application dependencies, validating issuer/audience/signature/expiry, and deciding permissions. Host startup fails with the protected operation names when either the filter or provider is missing. A denied runtime check faults with `permission_denied`.

## Inbox deduplication

For at-least-once one-way delivery, add `InboxDeduplicationFilter` to the server pipeline and
register its named `IMessageDeduplicationStore`. The filter requires `cl-message-id`, suppresses an
in-flight or completed duplicate, completes the inbox record only after handler success, and releases
failed work for redelivery. Use `RedisMessageDeduplicationStore` across replicas; the in-memory store
is process-local and is not a distributed guarantee.

## MCP is opt-in

Contract methods are not MCP tools unless marked `[McpTool]`, and no MCP listener is created unless the host explicitly calls one of:

```csharp
services.AddAnyProtocolMcp();
app.MapAnyProtocolMcp("/mcp");
```

or:

```csharp
services.AddAnyProtocolMcpStdio();
```

Treat MCP metadata (`ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`) as client-facing declarations, not enforcement. Apply host authentication/authorization to `/mcp`, limit exposed tools, and secure the parent process for stdio mode.

## TLS and transport ownership

| Transport | Security owner |
|---|---|
| REST | ASP.NET Core/Kestrel or reverse proxy for server TLS; `HttpClient` handler for client certificates and validation |
| gRPC | ASP.NET Core/Kestrel or reverse proxy; `GrpcChannel`/HTTP handler on the client |
| Kafka | `ProducerConfig`/`ConsumerConfig`, broker listeners, SASL/TLS, ACLs, and topic policy |
| RabbitMQ | `amqps` URI/client TLS plus broker virtual hosts, users, permissions, policies, and network controls |
| ZeroMQ | Deployment network or explicitly configured NetMQ security outside AnyProtocol; the built-in options expose endpoints only |
| InMemory | Process boundary and host permissions |

Never send `cl-auth-token` over plaintext or an untrusted broker/network. AnyProtocol does not reject insecure endpoints.

## Secrets and logging

- Read credentials from a secret provider or injected configuration; do not put them in source, topic names, transport names, contract names, or exception messages.
- Restrict Kafka configuration and REST/gRPC handler diagnostics from logging headers.
- Inject RabbitMQ URIs from a secret provider and redact user-info; restrict configure/write/read permissions to required exchange and queue patterns.
- `LoggingFilter` omits payloads and credentials by default.
- `AnyProtocolLogRedactor` can redact common token/password/key names and connection-string credentials, but it is not automatically applied to all application or third-party logs.
- `AnyProtocolLogRedactor.Payload(true, payload)` returns Base64 payload data. Enabling it is an explicit application decision and may expose personal or secret data.
- Kafka dead-letter headers include source and error type/message; ensure handler exception messages do not contain secrets and protect dead-letter topics.
- RabbitMQ dead letters preserve the original body and application headers while sanitizing framework error fields; restrict DLQ read and purge permissions.

## Threat-oriented deployment checklist

- Enforce TLS and authenticated peers at every network boundary.
- Validate tokens and permissions server-side; install `AuthorizationFilter` for every protected contract.
- Protect REST, gRPC, health, and MCP endpoints with deliberate routing and network policy.
- Configure Kafka SASL/TLS, ACLs, replication, retention, and dead-letter access; disable automatic topic creation where required.
- Configure RabbitMQ TLS, a dedicated virtual host/user, least privilege, queue limits, and DLQ access; do not expose the management UI publicly.
- Do not expose ZeroMQ bind ports publicly without an authenticated encrypted boundary.
- Treat messages as untrusted input; register request validators and install `ValidationFilter`
  explicitly, then cap payload/request sizes in hosts and brokers. See
  [validation configuration](CONFIGURATION.md#validation-error-handling-and-message-metadata).
- Bound inline, stored, and total-stream payload sizes; alert on storage failures and integrity faults.
- Keep retries limited to truly idempotent handlers and configure a durable inbox for at-least-once one-way delivery.
- Use generated `JsonSerializerContext` metadata for Native AOT and avoid permissive polymorphic deserialization.
- Bound shutdown and operation timeouts; alert on faults, retries, dead letters, and readiness loss.
- Review exporter and third-party instrumentation settings for header, query-string, payload, and exception leakage.
