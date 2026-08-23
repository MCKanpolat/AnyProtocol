# Getting started

## Expose one implementation over multiple protocols

Register the implementation once and select every server protocol with typed keys:

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddRestServer(ProtocolKey.Rest)
    .AddGrpcServer(ProtocolKey.Grpc)
    .AddServer<IOrders, Orders>(server => server.UseProtocols(
        ProtocolKey.Rest,
        ProtocolKey.Grpc,
        ProtocolKey.Mcp)));

builder.Services.AddAnyProtocolRest();
builder.Services.AddAnyProtocolGrpc();
builder.Services.AddAnyProtocolMcp();
// Configure a real application scheme; this example uses JWT bearer authentication.
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAnyProtocol();
app.MapAnyProtocolGrpc();
app.MapAnyProtocolMcp();
```

The HTTP MCP endpoint requires an authenticated caller by default. Add the appropriate
authentication package and replace the example scheme/configuration with your production
identity provider. `MapAnyProtocolMcpAllowAnonymousForDevelopment()` is available only for
explicit local-development use.

Each client selects exactly one registered protocol:

```csharp
link.AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Grpc));
```

To switch protocols per deployment, give the registration a stable key and overlay it from configuration:

```csharp
builder.Services.AddAnyProtocol(
    builder.Configuration.GetSection("AnyProtocol"),
    link => link
        .UseSerializer(new TextJsonMessageSerializer())
        .AddClient<IOrders>("OrdersClient", client =>
            client.UseProtocol(ProtocolKey.Rest)));
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

The JSON value wins over the code default. Invalid or unknown entries stop registration instead of being ignored.

`ProtocolKey.Create("orders-rest")` creates a custom key when an application needs a third-party protocol or multiple configured instances. String-based `UseTransport` and `AddTransport` overloads remain available for compatibility.

Configuration build fails if any selected protocol is missing or cannot support every operation in the contract. A contract also cannot select two REST server keys or two gRPC server keys because their native routes would be ambiguous. MCP is an explicit server protocol selection, exposes only `[McpTool]` methods, and host startup fails if its HTTP endpoint is not mapped or its adapter is not registered.

This guide starts with both ends of one contract in a single process, then changes only the transport registration and host wiring.

## 1. Create the InMemory application

```shell
dotnet new console -n Orders
cd Orders
dotnet add package Microsoft.Extensions.Hosting
dotnet add package AnyProtocol.DependencyInjection.Microsoft
dotnet add package AnyProtocol.Protocol.InMemory
dotnet add package AnyProtocol.Serializer.TextJson
```

Use the complete `Program.cs` from the [root quickstart](https://github.com/MCKanpolat/AnyProtocol#minimal-requestreply), then run:

```shell
dotnet run
```

`AddAnyProtocol` validates configuration immediately, registers the contract client as a singleton and server implementation as scoped, and adds a hosted service that starts and stops the bus with the host.

## 2. REST

Install `AnyProtocol.Protocol.Rest.AspNetCore` in the server and `AnyProtocol.Protocol.Rest` in the client. Keep the same `IOrders`, request, response, and server implementation.

Server registration in an ASP.NET Core app:

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddRestServer("rest")
    .AddServer<IOrders, Orders>(server => server.UseTransport("rest")));
builder.Services.AddAnyProtocolRest();

var app = builder.Build();
app.MapAnyProtocol("/api");
await app.RunAsync();
```

Client registration:

```csharp
var serializer = new TextJsonMessageSerializer();
var httpClient = new HttpClient
{
    BaseAddress = new Uri("https://orders.example")
};

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(serializer)
    .AddTransport("rest", new RestMessagingProtocol(httpClient, serializer, "/api"))
    .AddClient<IOrders>(client => client.UseTransport("rest")));
```

Each contract channel becomes `POST {routePrefix}/{channel}`. The default client and server prefix is `/anyprotocol`; both sides must use the same prefix. The client does not own or dispose the supplied `HttpClient`.

## 3. gRPC

Install `AnyProtocol.Protocol.Grpc.AspNetCore` in the server and `AnyProtocol.Protocol.Grpc` in the client.

Server:

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddGrpcServer("grpc")
    .AddServer<IOrders, Orders>(server => server.UseTransport("grpc")));
builder.Services.AddAnyProtocolGrpc();

var app = builder.Build();
app.MapAnyProtocolGrpc();
await app.RunAsync();
```

Client:

```csharp
var channel = GrpcChannel.ForAddress("https://orders.example");
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddTransport("grpc", new GrpcMessagingProtocol(channel, disposeChannel: true))
    .AddClient<IOrders>(client => client.UseTransport("grpc")));
```

`MapAnyProtocolGrpc` maps the fixed AnyProtocol unary and server-streaming service. The `disposeChannel` constructor argument defaults to `false`.

## 4. Kafka

Start the repository's single-node development broker and install the transport:

```shell
docker compose -f samples/docker-compose.yml up -d
dotnet add package AnyProtocol.Protocol.Kafka
```

Use the same registration in each process, adding only the client on callers and only the server on workers:

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddKafka(new KafkaProtocolOptions
    {
        BootstrapServers = "localhost:9092",
        TopicPrefix = "orders"
    })
    .AddClient<IOrders>(client => client.UseTransport("kafka"))
    .AddServer<IOrders, Orders>(server => server.UseTransport("kafka")));
```

Kafka request/reply is emulated with `_anyprotocol.reply.*` topics. Production deployments should set broker addresses, replication, authentication, and topic policy explicitly rather than using the development compose defaults.

## 5. RabbitMQ

Start the local broker and install the package:

```shell
docker compose -f samples/docker-compose.yml up -d rabbitmq
dotnet add package AnyProtocol.Protocol.RabbitMq
```

Switch only the transport registration and protocol selection:

```csharp
var rabbitMqUri = builder.Configuration["AnyProtocol:RabbitMq:Uri"]
    ?? "amqp://guest:guest@localhost:5672/"; // local development only

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddRabbitMq(new RabbitMqProtocolOptions
    {
        ConnectionUri = new Uri(rabbitMqUri),
        ExchangeName = "orders"
    })
    .AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.RabbitMq))
    .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.RabbitMq)));
```

Use injected `amqps` credentials and dedicated permissions in production. See the [complete RabbitMQ guide](RABBITMQ.md).

## 6. ZeroMQ

Install `AnyProtocol.Protocol.ZeroMq`. The server binds two endpoints:

```csharp
link.AddTransport("zeromq", new ZeroMqMessagingProtocol(new ZeroMqProtocolOptions
{
    Role = ZeroMqRole.Server,
    RouterEndpoint = "tcp://*:5555",
    PublisherEndpoint = "tcp://*:5556"
}));
link.AddServer<IOrders, Orders>(server => server.UseTransport("zeromq"));
```

Clients connect to those same router and publisher ports:

```csharp
link.AddTransport("zeromq", new ZeroMqMessagingProtocol(new ZeroMqProtocolOptions
{
    Role = ZeroMqRole.Client,
    RouterEndpoint = "tcp://orders.example:5555",
    PublisherEndpoint = "tcp://orders.example:5556"
}));
link.AddClient<IOrders>(client => client.UseTransport("zeromq"));
```

ZeroMQ request/reply and streaming are publish/subscribe emulations. It has no broker persistence or peer-authentication layer in AnyProtocol.

## Source generation and Native AOT

Dynamic builds can use the parameterless JSON serializer. Native AOT applications must include `AnyProtocol.Generator`, register contracts directly through generic builder methods, and provide a generated `JsonSerializerContext`:

```csharp
link.UseSerializer(new TextJsonMessageSerializer(AppJsonContext.Default));
```

The runnable [Native AOT sample](https://github.com/MCKanpolat/AnyProtocol/tree/develop/samples/AnyProtocol.AotSample) verifies generated proxy, server dispatch, metadata, serialization, streaming, faults, and MCP discovery.
