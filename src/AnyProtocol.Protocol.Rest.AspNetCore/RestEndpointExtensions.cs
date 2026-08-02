using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AnyProtocol.Protocol.Rest.AspNetCore;

/// <summary>
/// Provides extension methods for rest endpoint configuration and registration.
/// </summary>
public static class RestEndpointExtensions
{
    /// <summary>
    /// Adds rest server support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the add rest server operation.</returns>
    public static LinkBuilder AddRestServer(this LinkBuilder builder, string name = "rest")
        => AddRestServer(builder, ProtocolKey.Create(name));

    /// <summary>
    /// Adds rest server support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the add rest server operation.</returns>
    public static LinkBuilder AddRestServer(this LinkBuilder builder, ProtocolKey protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTransport(protocol, new RestServerProtocol());
    }

    /// <summary>
    /// Adds anyprotocol rest support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add anyprotocol rest operation.</returns>
    public static IServiceCollection AddAnyProtocolRest(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new RestEndpointMarker());
        return services;
    }

    /// <summary>
    /// Maps anyprotocol endpoints into the application pipeline.
    /// </summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <param name="routePrefix">The route prefix.</param>
    /// <returns>The result of the map anyprotocol operation.</returns>
    public static IEndpointRouteBuilder MapAnyProtocol(
        this IEndpointRouteBuilder endpoints,
        string routePrefix = "/anyprotocol")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var services = endpoints.ServiceProvider;
        _ = services.GetRequiredService<RestEndpointMarker>();
        var configuration = services.GetRequiredService<LinkConfiguration>();
        var descriptorFactory = services.GetRequiredService<ContractDescriptorFactory>();
        var registry = services.GetRequiredService<TransportRegistry>();
        foreach (var protocol in registry.Entries
                     .Select(static pair => pair.Value)
                     .OfType<RestServerProtocol>())
        {
            protocol.MarkMapped();
        }

        var routes = configuration.ServerRegistrations
            .SelectMany(
                registration => registration.Protocols
                    .Where(protocol =>
                        registry.TryGet(protocol, out var transport) &&
                        transport is RestServerProtocol)
                    .SelectMany(
                        protocol => descriptorFactory.Create(registration.ContractType).Methods
                            .Select(method => new RestRoute(registration, method, protocol))))
            .ToArray();
        var eventRoutes = configuration.EventRegistrations
            .Where(
                registration =>
                    registry.GetRequired(registration.TransportName) is RestServerProtocol)
            .Select(registration => new RestEventRoute(registration))
            .ToArray();
        var channels = routes.Select(route => route.Method.Channel)
            .Concat(eventRoutes.Select(route => route.Registration.Channel))
            .Distinct(StringComparer.Ordinal);

        foreach (var channel in channels)
        {
            ValidateChannel(channel);
            var routeTable = routes.Where(route => route.Method.Channel == channel).ToArray();
            var eventTable = eventRoutes
                .Where(route => route.Registration.Channel == channel)
                .ToArray();
            var pattern = $"{NormalizePrefix(routePrefix)}/{channel}";
            endpoints.MapPost(
                pattern,
                context => HandleRequestAsync(context, channel, routeTable, eventTable));
        }

        return endpoints;
    }

    private static async Task HandleRequestAsync(
        HttpContext httpContext,
        string channel,
        IReadOnlyList<RestRoute> routes,
        IReadOnlyList<RestEventRoute> eventRoutes)
    {
        var headers = new MessageHeaders();
        foreach (var header in httpContext.Request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        headers[HeaderNames.ContentType] = httpContext.Request.ContentType;
        headers[HeaderNames.Channel] ??= channel;
        headers[HeaderNames.MessageType] ??= MessageType.Request.ToString();
        headers[HeaderNames.MessageId] ??= Guid.NewGuid().ToString("N");
        headers[HeaderNames.CorrelationId] ??= headers[HeaderNames.MessageId];
        headers[HeaderNames.ReplyTo] = CaptureProtocol.ReplyChannel;

        var contractName = headers[HeaderNames.Contract];
        var methodName = headers[HeaderNames.Method];
        var messageType = headers.Get(HeaderNames.MessageType, MessageType.Request);
        var route = routes.SingleOrDefault(
            candidate =>
                candidate.Method.ContractName == contractName &&
                candidate.Method.MethodName == methodName);
        if (messageType != MessageType.Event && route is null && routes.Count == 1)
        {
            route = routes[0];
            headers[HeaderNames.Contract] = route.Method.ContractName;
            headers[HeaderNames.Method] = route.Method.MethodName;
        }
        var eventRoute = route is null && messageType == MessageType.Event
            ? eventRoutes.SingleOrDefault(
                candidate =>
                    (candidate.Registration.EventType.FullName ??
                     candidate.Registration.EventType.Name) == contractName)
            : null;

        if (route is null && eventRoute is null)
        {
            await WriteProblemAsync(
                    httpContext,
                    new FaultMessage(
                        "route_not_found",
                        $"No AnyProtocol route matches '{contractName}.{methodName}'."),
                    StatusCodes.Status404NotFound)
                .ConfigureAwait(false);
            return;
        }

        await using var bodyStream = new MemoryStream();
        await httpContext.Request.Body.CopyToAsync(bodyStream, httpContext.RequestAborted)
            .ConfigureAwait(false);
        var envelope = new TransportEnvelope(headers, bodyStream.ToArray());
        var capture = new CaptureProtocol();
        var dispatcher = httpContext.RequestServices.GetRequiredService<MessageDispatcher>();
        if (eventRoute is not null)
        {
            await dispatcher.DispatchEventAsync(
                    eventRoute.Registration,
                    envelope,
                    capture,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        else
        {
            await dispatcher.DispatchAsync(
                    route!.Registration,
                    route.Method,
                    envelope,
                    capture,
                    httpContext.RequestAborted,
                    route.Protocol)
                .ConfigureAwait(false);
        }

        if (capture.Response is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var response = capture.Response;
        if (response.Headers.Get(HeaderNames.MessageType, MessageType.Response) == MessageType.Fault)
        {
            var serializer = httpContext.RequestServices
                .GetRequiredService<LinkConfiguration>()
                .Serializer!;
            var fault = serializer.Deserialize<FaultMessage>(response.Body) ??
                        new FaultMessage("handler_failed", "The handler returned an empty fault.");
            await WriteProblemAsync(httpContext, fault, GetStatusCode(fault.Code))
                .ConfigureAwait(false);
            return;
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        foreach (var header in response.Headers)
        {
            if (!string.Equals(header.Key, HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                httpContext.Response.Headers[header.Key] = header.Value;
            }
        }

        httpContext.Response.ContentType =
            response.Headers[HeaderNames.ContentType] ?? "application/octet-stream";
        await httpContext.Response.Body.WriteAsync(response.Body, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        FaultMessage fault,
        int statusCode)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = fault.Code,
            ["retryable"] = fault.Retryable,
            ["exceptionType"] = fault.ExceptionType,
            ["details"] = fault.Details
        };
        var result = Results.Problem(
            detail: fault.Message,
            statusCode: statusCode,
            title: "AnyProtocol request failed",
            extensions: extensions);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private static int GetStatusCode(string code)
        => code switch
        {
            "validation_failed" => StatusCodes.Status400BadRequest,
            "permission_denied" => StatusCodes.Status403Forbidden,
            "route_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };

    private static string NormalizePrefix(string routePrefix)
    {
        var normalized = routePrefix.Trim('/');
        return normalized.Length == 0 ? string.Empty : $"/{normalized}";
    }

    private static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) ||
            channel.Contains('{') ||
            channel.Contains('}') ||
            channel.Contains('?') ||
            channel.Contains('#') ||
            channel.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Channel '{channel}' cannot be represented as a REST route.");
        }
    }

    private sealed record RestRoute(
        ServerRegistration Registration,
        ContractMethodDescriptor Method,
        ProtocolKey Protocol);

    private sealed record RestEventRoute(EventRegistration Registration);

    private sealed class RestEndpointMarker;

    private sealed class CaptureProtocol : IMessagingProtocol
    {
        public const string ReplyChannel = "_anyprotocol.http.response";

        public TransportEnvelope? Response { get; private set; }

        public TransportCapabilities Capabilities =>
            TransportCapabilities.NativeHeaders |
            TransportCapabilities.NativeRequestReply;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(channel, ReplyChannel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected HTTP reply channel '{channel}'.");
            }

            Response = envelope;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
