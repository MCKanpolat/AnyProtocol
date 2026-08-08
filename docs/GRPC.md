# gRPC protocol

`AnyProtocol.Protocol.Grpc` provides the client transport and `AnyProtocol.Protocol.Grpc.AspNetCore` hosts the fixed AnyProtocol unary and server-streaming service.

## Install

```shell
# Server
dotnet add package AnyProtocol.Protocol.Grpc.AspNetCore

# Client
dotnet add package AnyProtocol.Protocol.Grpc
```

## Server

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddGrpcServer(ProtocolKey.Grpc)
    .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.Grpc)));
builder.Services.AddAnyProtocolGrpc();

var app = builder.Build();
app.MapAnyProtocolGrpc();
await app.RunAsync();
```

## Client

```csharp
var channel = GrpcChannel.ForAddress("https://orders.example");

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddTransport(
        ProtocolKey.Grpc,
        new GrpcMessagingProtocol(channel, disposeChannel: true))
    .AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Grpc)));
```

`disposeChannel` defaults to `false`; set it to `true` only when the transport owns the channel.

## Semantics

- Native request/reply and native server streaming.
- At-most-once delivery and volatile storage.
- No publish/subscribe or competing consumer groups.
- Server readiness becomes healthy only after `MapAnyProtocolGrpc()` maps the endpoint.

See [Transport semantics](TRANSPORT_SEMANTICS.md) for readiness, cancellation, and capability details.
