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

var app = builder.Build();
app.MapAnyProtocolMcp("/mcp");
await app.RunAsync();
```

Mark only the operations intended for tool discovery:

```csharp
[McpTool]
public Task<Order> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    => repository.GetAsync(orderId, cancellationToken);
```

For process integrations, use `AddAnyProtocolMcpStdio()` instead of the HTTP registration and mapping.

## Important behavior

- MCP is a server exposure protocol; it is not a client transport.
- Startup validation requires the MCP adapter to be registered and its endpoint to be mapped.
- Contract methods without `[McpTool]` are not exposed.
- Apply authorization and input validation to tools like any other public endpoint.

See [Security](SECURITY.md) and [Getting started](GETTING_STARTED.md) for endpoint and contract setup.
