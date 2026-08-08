# REST / HTTP protocol

`AnyProtocol.Protocol.Rest` sends contract calls as HTTP requests. `AnyProtocol.Protocol.Rest.AspNetCore` maps the same contract on an ASP.NET Core server without requiring transport-specific handlers.

## Install

```shell
# Server
dotnet add package AnyProtocol.Protocol.Rest.AspNetCore

# Client
dotnet add package AnyProtocol.Protocol.Rest
```

## Server

```csharp
builder.Services.AddAnyProtocol(link => link
    .UseSerializer(new TextJsonMessageSerializer())
    .AddRestServer(ProtocolKey.Rest)
    .AddServer<IOrders, Orders>(server => server.UseProtocols(ProtocolKey.Rest)));
builder.Services.AddAnyProtocolRest();

var app = builder.Build();
app.MapAnyProtocol("/api");
await app.RunAsync();
```

## Client

```csharp
var serializer = new TextJsonMessageSerializer();
var httpClient = new HttpClient
{
    BaseAddress = new Uri("https://orders.example")
};

builder.Services.AddAnyProtocol(link => link
    .UseSerializer(serializer)
    .AddTransport(
        ProtocolKey.Rest,
        new RestMessagingProtocol(httpClient, serializer, "/api"))
    .AddClient<IOrders>(client => client.UseProtocol(ProtocolKey.Rest)));
```

Each contract channel becomes `POST {routePrefix}/{channel}`. The default prefix is `/anyprotocol`; the client and server must use the same prefix. The supplied `HttpClient` remains owned by the application.

## Semantics and limits

- Native request/reply with HTTP status and problem details for faults.
- Events can be posted to the mapped server endpoint.
- REST does not expose subscriptions or streaming contracts.
- Channels cannot contain `{`, `}`, `?`, `#`, empty path segments, or whitespace-only segments.

See [Getting started](GETTING_STARTED.md) for the same contract wired over multiple protocols and [Transport semantics](TRANSPORT_SEMANTICS.md) for the capability matrix.
