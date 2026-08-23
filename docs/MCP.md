# MCP protocol

MCP is an explicit server protocol that exposes selected AnyProtocol contract methods as Model Context Protocol tools. A method is published only when it has the `[McpTool]` attribute; selecting MCP does not expose every contract method automatically.

## Install

```shell
dotnet add package AnyProtocol.Mcp.AspNetCore
```

## HTTP endpoint

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
        .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.Mcp)));
builder.Services.AddAnyProtocolMcp();
// Configure a real application scheme; this example uses JWT bearer authentication.
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAnyProtocolMcp("/mcp");
await app.RunAsync();
```

Mark only the operations intended for tool discovery:

```csharp
[Channel("orders")]
public interface IOrders
{
    [Idempotent]
    [McpTool(
        Name = "orders_get",
        Description = "Reads an order by identifier.",
        ReadOnly = true)]
    ValueTask<Order> GetAsync(
        GetOrderRequest request,
        CancellationToken cancellationToken);
}

public sealed record GetOrderRequest(string OrderId);
public sealed record Order(string Id, string Status);
```

Put `McpTool` on the contract interface method. MCP compiles its catalog from the
contract descriptor; putting the attribute only on an implementation method does not
select the operation. The input request must serialize to a JSON object, so use a
request DTO/record (or `EmptyRequest` for a no-input tool) rather than a scalar parameter.

For process integrations, use `AddAnyProtocolMcpStdio()` instead of the HTTP registration and mapping.

## Important behavior

- MCP is a server exposure protocol; it is not a client transport.
- Startup validation requires the MCP adapter to be registered and its endpoint to be mapped.
- Contract methods without `[McpTool]` are not exposed.
- Tool names are optional; the default is a bounded snake_case name derived from the contract and
  method. Explicit names must be 1–128 ASCII letters, digits, `.`, `_`, or `-`.
- Server-streaming methods cannot be published as MCP tools. Tool input schemas must be objects;
  non-object output values are wrapped as an object result.
- `ReadOnly`, `Destructive`, `Idempotent`, and `OpenWorld` are client-facing metadata hints.
  Authorization, validation, and side-effect controls remain host responsibilities.
- HTTP MCP endpoints require ASP.NET Core authorization by default. Configure authentication,
  register the required policy, and call `UseAuthorization()` before serving requests.
- `MapAnyProtocolMcpAllowAnonymousForDevelopment()` is an explicitly named local-development
  opt-out; never use it for an internet-facing endpoint. Stdio has a separate process boundary.

See [Security](SECURITY.md) and [Getting started](GETTING_STARTED.md) for endpoint and contract setup.
