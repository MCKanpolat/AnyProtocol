<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/AnyProtocol_logo_dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/assets/AnyProtocol_logo_light.svg">
    <img alt="AnyProtocol" src="docs/assets/AnyProtocol_logo_dark.svg" width="420">
  </picture>
</p>

# AnyProtocol

AnyProtocol is a .NET contract-first messaging library. A single interface can be registered as a local service or carried over REST, gRPC, Kafka, RabbitMQ, or ZeroMQ, with request/reply, events, generated proxies, transport validation, health checks, and built-in diagnostics.

One logical server registration can be exposed through several protocols at the same time. Each client selects one protocol in its own configuration; neither the contract nor the service implementation contains transport-specific code.

![AnyProtocol overview: one contract, any transport](docs/assets/AnyProtocol_overview.png)

> **Stability:** The repository targets .NET 10 and the public API is still evolving. Pin package versions, validate transport semantics for your workload, and use the source generator plus serializer metadata before Native AOT deployment. Transport support is intentionally capability-based; a feature listed for one transport is not implied for the others.

## Transport matrix

| Transport | Request/reply | Events/pub-sub | Streaming | Delivery | Durability | Ordering |
|---|---:|---:|---:|---|---|---|
| InMemory | Emulated | Yes | Emulated | At most once | Volatile | Per channel |
| REST | Native | No | No | At most once | Volatile | None |
| gRPC | Native | No | Native server streaming | At most once | Volatile | None |
| Kafka | Emulated | Yes | Emulated | At least once | Durable | Per partition |
| RabbitMQ | Emulated | Yes | Emulated | At least once | Durable | Per channel¹ |
| ZeroMQ | Emulated | Yes | Emulated | At most once | Volatile | Per channel |

¹ Per-channel ordering requires one active consumer/handler for the queue; concurrent handlers can complete out of order.

See [transport semantics](https://mckanpolat.github.io/AnyProtocol/TRANSPORT_SEMANTICS.html) before choosing a production transport.

## Prerequisites

- .NET SDK `10.0.300` or a compatible newer feature band
- A Kafka broker for Kafka transport
- A RabbitMQ broker for RabbitMQ transport
- Reachable TCP endpoints for ZeroMQ
- ASP.NET Core hosting for REST or gRPC servers

From source:

```shell
git clone https://github.com/MCKanpolat/AnyProtocol.git
cd AnyProtocol
dotnet restore AnyProtocol.slnx
```

For an application using Microsoft DI and the local transport:

```shell
dotnet add package AnyProtocol.DependencyInjection.Microsoft
dotnet add package AnyProtocol.Protocol.InMemory
dotnet add package AnyProtocol.Serializer.TextJson
```

## Package map

| Purpose | Package |
|---|---|
| Contracts and core pipeline | `AnyProtocol`, `AnyProtocol.Abstraction` |
| Microsoft DI and hosted lifecycle | `AnyProtocol.DependencyInjection.Microsoft` |
| JSON / MessagePack | `AnyProtocol.Serializer.TextJson`, `AnyProtocol.Serializer.MessagePack` |
| In-process transport | `AnyProtocol.Protocol.InMemory` |
| REST client / ASP.NET Core server | `AnyProtocol.Protocol.Rest`, `AnyProtocol.Protocol.Rest.AspNetCore` |
| gRPC client / ASP.NET Core server | `AnyProtocol.Protocol.Grpc`, `AnyProtocol.Protocol.Grpc.AspNetCore` |
| Kafka / RabbitMQ / ZeroMQ | `AnyProtocol.Protocol.Kafka`, `AnyProtocol.Protocol.RabbitMq`, `AnyProtocol.Protocol.ZeroMq` |
| Optional MCP exposure | `AnyProtocol.Mcp`, `AnyProtocol.Mcp.AspNetCore` |
| Compile-time contract generation | `AnyProtocol.Generator` |

## Minimal request/reply

```csharp
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddTransport("default", new InMemoryMessagingProtocol())
    .AddClient<IOrders>()
    .AddServer<IOrders, Orders>());

using var host = builder.Build();
await host.StartAsync();

var client = host.Services.GetRequiredService<IOrders>();
var result = await client.GetAsync(new GetOrder("42"), CancellationToken.None);
Console.WriteLine(result.Status);

await host.StopAsync();

[Channel("orders")]
public interface IOrders
{
    [Idempotent]
    ValueTask<Order> GetAsync(GetOrder request, CancellationToken cancellationToken);
}

public sealed record GetOrder(string Id);
public sealed record Order(string Id, string Status);

public sealed class Orders : IOrders
{
    public ValueTask<Order> GetAsync(GetOrder request, CancellationToken cancellationToken)
        => ValueTask.FromResult(new Order(request.Id, "ready"));
}
```

## One service, multiple protocols

Use the built-in `ProtocolKey` values for compile-time-friendly configuration. Custom keys remain available for third-party protocols or named instances used by different registrations.

```csharp
link.AddRestServer(ProtocolKey.Rest)
    .AddGrpcServer(ProtocolKey.Grpc)
    .AddServer<IOrders, Orders>(server => server.UseProtocols(
        ProtocolKey.Rest,
        ProtocolKey.Grpc,
        ProtocolKey.Mcp));
```

The host maps each selected endpoint once:

```csharp
services.AddAnyProtocolRest();
services.AddAnyProtocolGrpc();
services.AddAnyProtocolMcp();

app.MapAnyProtocol();
app.MapAnyProtocolGrpc();
app.MapAnyProtocolMcp();
```

A gRPC client and a REST client keep the same `IOrders` contract but choose independently:

```csharp
link.AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Grpc));
// In another application:
link.AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Rest));
```

The choice can come from deployment configuration without changing the contract registration:

```csharp
services.AddAnyProtocol(
    configuration.GetSection("AnyProtocol"),
    link => link.AddClient<IOrders>("OrdersClient"));
```

```json
{
  "AnyProtocol": {
    "Clients": {
      "OrdersClient": { "Protocol": "Grpc" }
    }
  }
}
```

Every selected protocol is validated against every contract operation during startup. An incompatible selection fails configuration instead of exposing a partial service. Duplicate native REST/gRPC bindings for one contract are rejected. MCP publishes only methods marked with `[McpTool]`; selecting MCP without registering and hosting its adapter stops application startup.

`[Idempotent]` permits configured retries; it does not provide deduplication.

## Documentation and samples

- [Getting started and transport switching](https://mckanpolat.github.io/AnyProtocol/GETTING_STARTED.html)
- [Configuration reference](https://mckanpolat.github.io/AnyProtocol/CONFIGURATION.html)
- [RabbitMQ guide](https://mckanpolat.github.io/AnyProtocol/RABBITMQ.html)
- [Transport semantics](https://mckanpolat.github.io/AnyProtocol/TRANSPORT_SEMANTICS.html)
- [Performance methodology](https://mckanpolat.github.io/AnyProtocol/PERFORMANCE.html)
- [Deployment](https://mckanpolat.github.io/AnyProtocol/DEPLOYMENT.html)
- [Observability](https://mckanpolat.github.io/AnyProtocol/OBSERVABILITY.html)
- [Security](https://mckanpolat.github.io/AnyProtocol/SECURITY.html)
- [API reference](https://mckanpolat.github.io/AnyProtocol/api/)
- [OrderSystem multi-protocol reference](https://github.com/MCKanpolat/AnyProtocol/tree/develop/samples/OrderSystem.MultiProtocol)
- [OrderSystem MCP/REST host](https://github.com/MCKanpolat/AnyProtocol/tree/develop/samples/OrderSystem.McpHost)
- [Native AOT smoke sample](https://github.com/MCKanpolat/AnyProtocol/tree/develop/samples/AnyProtocol.AotSample)

Run the samples with:

```shell
dotnet run --project samples/OrderSystem.McpHost
dotnet run --project samples/OrderSystem.McpHost -- --stdio
dotnet run --project samples/AnyProtocol.AotSample
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Server
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Client -- --protocol Rest
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Client -- --protocol Grpc
```

The multi-protocol order server uses `http://localhost:5080` for REST/MCP and `http://localhost:5081` for gRPC.

## License

AnyProtocol is licensed under [LGPL-3.0-or-later](LICENSE). Applications may
link to the AnyProtocol libraries under their own license terms. Distributed
changes to the AnyProtocol libraries themselves must remain available under
LGPL-3.0-or-later with their corresponding source code.

The AnyProtocol name and logo are governed separately by the
[trademark policy](TRADEMARKS.md). Forks must not present themselves as the
official AnyProtocol project.
