# OrderSystem multi-protocol reference

This runnable reference exposes one `IOrderService` contract through REST, gRPC, and MCP. The REST and gRPC clients run the same create, get, and list request/reply scenario; the selected transport changes at the client boundary, not in the contract or service.

## Project boundaries

The sample keeps each responsibility visible:

- `OrderSystem.Contracts` contains the `IOrderService` contract and its `Models` request, response, and fault types. It is transport-independent.
- `OrderSystem.Server` contains the in-memory `Repositories`, domain `Services`, and `Hosting` composition that maps REST, gRPC, MCP, and health endpoints.
- `OrderSystem.Client` contains command-line `Configuration` and the shared `Services/OrderScenario` that drives either REST or gRPC.
- `tests/OrderSystem.MultiProtocol.Tests` covers client configuration, the common scenario, REST/gRPC interoperability, fault propagation, MCP tool discovery, endpoint isolation, discovery, liveness, and readiness.

The request/reply capability is the same across REST and gRPC: `CreateAsync(CreateOrderRequest)`, `GetAsync(GetOrderRequest)`, and `ListAsync(ListOrdersRequest)`. A newly started server contains the seeded `ORD-1000` order; each client run creates its own next order.

## Prerequisites and build

Install the .NET 10 SDK. From the repository root, restore and build the solution:

```powershell
dotnet restore AnyProtocol.slnx
dotnet build AnyProtocol.slnx --no-restore
```

## Run the server and clients

From the repository root, start the server in one terminal:

```powershell
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Server
```

Then run either client in a second terminal:

```powershell
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Client -- --protocol Rest
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Client -- --protocol Grpc
dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Client -- --protocol Rest --show-error
```

Both protocol selections create an order, retrieve it, and list orders. Typical output is:

```text
Protocol: Rest
Created order: ORD-1001 (ready)
Retrieved order: ORD-1001 (ready)
Listed orders: 2
```

The precise order ID and list count increase as the long-running server accepts more client runs. `--show-error` also performs a lookup for `ORD-404` and prints:

```text
Expected fault: order_not_found - The requested order was not found.
```

`Rest` is the default when no protocol is configured. Protocol values are case-insensitive, but only `Rest` and `Grpc` are supported. An invalid value reports the supported values. If the server is not running, the client reports the selected server URL and prints the server-start command above. `OrderSystem:RestUri` defaults to `http://localhost:5080`; `OrderSystem:GrpcUri` defaults to `http://localhost:5081`.

If port `5080` or `5081` is already in use, stop the conflicting process or change the matching server endpoint and client URI together. The REST/MCP endpoint is intentionally HTTP/1-only on 5080, while the gRPC endpoint is HTTP/2-only on 5081; this keeps a non-TLS local gRPC connection from falling back to HTTP/1.1.

## HTTP and health endpoints

The server uses two local HTTP endpoints: REST, MCP, discovery, and health use `http://localhost:5080` (HTTP/1), while gRPC uses `http://localhost:5081` (HTTP/2-only).

- [`/`](http://localhost:5080/) is the discovery root. It returns the REST base path (`/api`), gRPC method (`AnyProtocol.Transport/Unary`), MCP endpoint (`/mcp`), and liveness/readiness paths; use the gRPC client URI `http://localhost:5081` for that method.
- [`/health/live`](http://localhost:5080/health/live) checks the live-tagged AnyProtocol health checks.
- [`/health/ready`](http://localhost:5080/health/ready) checks the ready-tagged AnyProtocol health checks.
- [`/mcp`](http://localhost:5080/mcp) is the Streamable HTTP MCP endpoint.

With the server running, visit the discovery root and both health URLs, or use a compatible HTTP client. Each health endpoint returns `Healthy` when the sample has started successfully.

## MCP tools

Connect any compatible MCP client to `http://localhost:5080/mcp`, use its tool-listing operation to discover the input schemas, then call the tools with JSON arguments. This avoids assuming a particular inspector command-line syntax while using the endpoint's advertised schema.

| Tool | Purpose | Example JSON arguments |
|---|---|---|
| `orders_create` | Creates an order. | `{ "customerId": "customer-42", "total": 125.50 }` |
| `orders_get` | Gets an order by ID. | `{ "orderId": "ORD-1000" }` |
| `orders_list` | Lists all current orders. | `{}` |

For a manual MCP workflow: start the server, configure the client with the Streamable HTTP endpoint above, list tools, confirm these three tool names and the advertised schemas, call `orders_create`, pass its returned `orderId` to `orders_get`, and call `orders_list`. MCP exposes only the three methods marked with `[McpTool]` on the contract.

## Further reading

- [Transport semantics](../../docs/TRANSPORT_SEMANTICS.md)
- [Configuration](../../docs/CONFIGURATION.md)
- [Observability](../../docs/OBSERVABILITY.md)
- [Security](../../docs/SECURITY.md)
- [Deployment](../../docs/DEPLOYMENT.md)
