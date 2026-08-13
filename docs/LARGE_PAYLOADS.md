# Large payload offload

Large payload offload keeps oversized serialized request, event, response, and stream-item bodies
outside a messaging transport. It is disabled by default. Inline envelopes are unchanged when the
feature is disabled or when a body is at or below the configured threshold.

## Configure a Redis store

For local Redis conformance testing, start the repository's Redis service and run
the provider test with its connection string:

```shell
docker compose -f samples/docker-compose.yml up -d --wait redis
```

PowerShell:

```powershell
$env:ANYPROTOCOL_REDIS_CONNECTION = "localhost:6379"
dotnet test tests/AnyProtocol.Storage.Redis.Tests/AnyProtocol.Storage.Redis.Tests.csproj
```

POSIX shells:

```shell
ANYPROTOCOL_REDIS_CONNECTION=localhost:6379 \
  dotnet test tests/AnyProtocol.Storage.Redis.Tests/AnyProtocol.Storage.Redis.Tests.csproj
```

Register the provider before `AddAnyProtocol`, then enable an explicit policy:

```csharp
builder.Services.AddAnyProtocolRedisPayloadStore(
    "large-payloads",
    options =>
    {
        options.ConnectionString = redisConnectionString;
        options.KeyPrefix = "orders:production:payload";
    });

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .UseLargePayloadOffload(options =>
    {
        options.StoreName = "large-payloads";
        options.MaxInlinePayloadBytes = 512 * 1024;
        options.MaxStoredPayloadBytes = 64 * 1024 * 1024;
        options.TimeToLive = TimeSpan.FromMinutes(15);
    })
    .AddKafka(kafkaOptions));
```

All four policy values are required. `MaxStoredPayloadBytes` also bounds the cumulative serialized
bytes produced or consumed by one stream. Select limits from broker bounds, Redis capacity,
application concurrency, and the maximum retry/recovery window; the example values are not
framework defaults.

Enabling a policy with no matching `ILargePayloadStore`, duplicate logical store names,
non-positive limits/TTL, or a maximum that is not greater than the inline threshold fails startup.
The store name is logical and case-insensitive.

## Wire and delivery behavior

AnyProtocol serializes first. Bodies above the inline threshold are stored under a deterministic
message-ID-derived key before transport publication. The broker or native transport receives an
empty body plus `cl-payload-mode=stored`, store name, opaque key, length, SHA-256, and expiry
headers. Receivers load and verify the body before deserialization, filters, or handler dispatch.

The same message ID and digest is an idempotent write. Reusing an ID for different content fails
with `payload_id_conflict`. A transport failure after a successful write leaves the object for TTL
expiry; immediate deletion could race an uncertain acknowledgement or retry. Reads never delete,
so duplicate delivery, competing consumers, retries, and event fan-out remain safe.

Stable failure codes are:

| Code | Meaning | Retryable |
|---|---|---:|
| `payload_not_found` | Missing or expired reference | No |
| `payload_store_unavailable` | Temporary provider failure | Yes |
| `payload_integrity_failed` | Length, digest, or provider record mismatch | No |
| `payload_id_conflict` | Same message ID, different content | No |
| `payload_store_not_configured` | Receiver lacks the named provider | No |
| `payload_reference_invalid` | Required reference metadata is malformed | No |
| `payload_too_large` | Per-message or total-stream bound exceeded | No |

## Redis sizing and security

- Use a key prefix that separates application, environment, and tenant where applicable.
- Keep TTL at least as long as broker retries, dead-letter recovery, and expected consumer outages.
- Budget Redis memory for payload size multiplied by peak retained concurrency; Redis is best for
  moderate, short-lived bodies. Add an object-storage provider for larger or longer-lived objects.
- Restrict Redis with network isolation, TLS where available, ACLs, and least-privilege credentials.
- Never put connection strings or endpoints into headers. The Redis provider hashes message IDs in
  physical keys and does not log bodies, credentials, or full locators.
- Authorization is evaluated after hydration through the normal server pipeline. Length and
  SHA-256 are verified before deserialization.

The current serializer and store contracts use `ReadOnlyMemory<byte>`, so a complete serialized
body is resident in memory at sender and receiver. Offload reduces broker load; it is not unbounded
streaming storage.
