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

### OpenAPI / Swagger

The REST host can optionally expose one typed operation endpoint per contract method. The existing channel endpoint remains available for AnyProtocol clients.

```shell
dotnet add package Swashbuckle.AspNetCore
```

Replace the `AddAnyProtocolRest()` registration above with the configured form:

```csharp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAnyProtocolRest(options =>
{
    options.MapOperationEndpoints = true;
    options.DocumentationContentType = "application/json";
    options.FallbackOperationRouteResolver = method =>
    {
        var methodName = method.MethodName.EndsWith("Async", StringComparison.Ordinal)
            ? method.MethodName[..^5]
            : method.MethodName;
        return $"v1/{method.Channel}/{methodName.ToLowerInvariant()}";
    };
    options.FallbackOperationHttpMethodResolver = method =>
        method.MethodName.StartsWith("Get", StringComparison.Ordinal)
            ? "GET"
            : "POST";
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapAnyProtocol("/api");
```

For a contract with the `orders` channel, operation routes are exposed with the configured HTTP method. For example, `CreateAsync` can be documented as `POST /api/orders/create`. These endpoints add the AnyProtocol contract, method, and channel headers internally before dispatching to the same server handler used by the channel endpoint.

Route names can be explicitly controlled with `RestRouteAttribute`. A contract-level attribute supplies the route prefix and a method-level attribute supplies the operation segment:

```csharp
[RestRoute("orders")]
public interface IOrders
{
    [RestRoute("create-order")]
    ValueTask<OrderResponse> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken);
}
```

Explicit method and contract routes take precedence. `FallbackOperationRouteResolver` is used only when no explicit route metadata is present; its default preserves the previous channel-plus-kebab-case behavior.

ASP.NET Core route attributes are also supported. They are read from the registered implementation type and method, so existing controller-style declarations can be reused:

```csharp
using Microsoft.AspNetCore.Mvc;

[Route("orders")]
public sealed class Orders : IOrders
{
    [HttpPost("create-order")]
    public ValueTask<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // Handle the request.
    }
}
```

`[Route]` supplies the service prefix and `[HttpPost("...")]` supplies both the operation route and its HTTP method. `[HttpGet]`, `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]`, and other HTTP method attributes are supported. Put the HTTP method attribute on the contract method when the AnyProtocol REST client should infer the method automatically; the server also reads it from the implementation method. Route templates must be static: route parameters, constraints, token replacement such as `[controller]`, query strings, and fragments are not supported.

The library-specific alternative is `[RestHttpMethod]`:

```csharp
public interface IOrders
{
    [RestRoute("orders/create")]
    [RestHttpMethod(RestHttpMethods.Post)]
    ValueTask<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken);
}
```

Use the `RestHttpMethods` constants for standard methods (`Get`, `Post`, `Put`, `Patch`, `Delete`, `Head`, `Options`, `Trace`, and `Connect`). The string constructor remains available for extension methods such as `PROPFIND`; values are trimmed, normalized to uppercase, and rejected when they are not valid HTTP method tokens.

HTTP method precedence is method-level `RestHttpMethodAttribute`/ASP.NET Core metadata, then `FallbackOperationHttpMethodResolver`. If no method is configured, the default is `POST`. The client uses the same rule through the optional resolver passed to `RestMessagingProtocol`:

```csharp
var transport = new RestMessagingProtocol(
    httpClient,
    serializer,
    "/api",
    method => method.MethodName.StartsWith("Get", StringComparison.Ordinal)
        ? RestHttpMethods.Get
        : RestHttpMethods.Post);
```

The server maps the native channel endpoint with the resolved verb as well. Therefore a `GET`, `PUT`, or `PATCH` method is sent to `/{routePrefix}/{channel}` using that verb; operation endpoints use the configured route and the same verb. Events remain `POST` because they do not have a contract method operation.

The route precedence is method-level explicit metadata, service-level prefix metadata, then `FallbackOperationRouteResolver`. Both `RestRouteAttribute` and ASP.NET Core metadata can be used in the same application. If two explicit declarations at the same level produce different templates or HTTP methods, endpoint mapping fails with an actionable error instead of silently selecting one. Multiple operations may share the same route when their HTTP methods differ; the route and HTTP method combination must be unique.

`MapOperationEndpoints` defaults to `false`, so existing applications keep their current route surface. The operation endpoints publish standard ASP.NET Core endpoint metadata for request bodies, successful responses, and problem details; Swagger, Microsoft OpenAPI, and other compatible document generators can consume that metadata without an AnyProtocol-specific Swagger dependency.

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

Each contract channel becomes `{HTTP_METHOD} {routePrefix}/{channel}` according to the method metadata. The default method is `POST`, and the default prefix is `/anyprotocol`; the client and server must use the same prefix and method resolver. The supplied `HttpClient` remains owned by the application.

## Semantics and limits

- Native request/reply with HTTP status and problem details for faults.
- Events can be posted to the mapped server endpoint.
- REST does not expose subscriptions or streaming contracts.
- Channels cannot contain `{`, `}`, `?`, `#`, empty path segments, or whitespace-only segments.
- Request bodies are limited to 10 MiB by default. Set `RestEndpointOptions.MaxRequestBodyBytes`
  to the smallest practical value and match it in Kestrel, IIS, reverse proxies, ingress, and
  load balancers. Both declared and chunked bodies are enforced; oversized requests receive
  `413` with the stable `request_body_too_large` code.
- Unexpected handler failures return a sanitized `handler_failed` problem with an `errorId`.
  Use that identifier to correlate server-side logs; exception types, messages, and stack details
  are not exposed to clients.

See [Getting started](GETTING_STARTED.md) for the same contract wired over multiple protocols and [Transport semantics](TRANSPORT_SEMANTICS.md) for the capability matrix.
