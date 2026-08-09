using System.Reflection;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
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
    /// <param name="configure">The optional REST endpoint configuration.</param>
    /// <returns>The result of the add anyprotocol rest operation.</returns>
    public static IServiceCollection AddAnyProtocolRest(
        this IServiceCollection services,
        Action<RestEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RestEndpointOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.DocumentationContentType))
        {
            throw new ArgumentException(
                "The documented payload content type cannot be empty.",
                nameof(configure));
        }

        services.AddSingleton(new RestEndpointMarker());
        services.AddSingleton(options);
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
        var options = services.GetService<RestEndpointOptions>() ?? new RestEndpointOptions();
        var configuration = services.GetRequiredService<LinkConfiguration>();
        var descriptorFactory = services.GetRequiredService<ContractDescriptorFactory>();
        var registry = services.GetRequiredService<TransportRegistry>();
        var operationRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var routesByHttpMethod = routeTable
                .GroupBy(
                    route => GetOperationHttpMethod(route, options),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<RestRoute>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            if (eventTable.Length > 0)
            {
                routesByHttpMethod.TryAdd("POST", []);
            }

            foreach (var (httpMethod, methodRoutes) in routesByHttpMethod)
            {
                endpoints.MapMethods(
                    pattern,
                    [httpMethod],
                    context => HandleRequestAsync(
                        context,
                        channel,
                        methodRoutes,
                        httpMethod == "POST" ? eventTable : []));
            }

            if (options.MapOperationEndpoints)
            {
                foreach (var route in routeTable)
                {
                    MapOperationEndpoint(
                        endpoints,
                        route,
                        routePrefix,
                        options,
                        operationRoutes);
                }
            }
        }

        return endpoints;
    }

    private static void MapOperationEndpoint(
        IEndpointRouteBuilder endpoints,
        RestRoute route,
        string routePrefix,
        RestEndpointOptions options,
        ISet<string> operationRoutes)
    {
        var httpMethod = GetOperationHttpMethod(route, options);
        var pattern = $"{NormalizePrefix(routePrefix)}/{GetOperationRoute(route, options)}";
        if (!operationRoutes.Add($"{httpMethod} {pattern}"))
        {
            throw new InvalidOperationException(
                $"The REST operation route '{httpMethod} {pattern}' is registered more than once. " +
                "Use a unique HTTP method and route combination for each operation endpoint.");
        }

        var endpoint = endpoints.MapMethods(
            pattern,
            [httpMethod],
            (Delegate)(Func<HttpContext, Task>)(context =>
            {
                context.Request.Headers[HeaderNames.Contract] = route.Method.ContractName;
                context.Request.Headers[HeaderNames.Method] = route.Method.MethodName;
                context.Request.Headers[HeaderNames.Channel] = route.Method.Channel;
                return HandleRequestAsync(context, route.Method.Channel, [route], []);
            }));

        endpoint
            .WithName(GetOperationName(route.Method))
            .WithTags(GetContractTag(route.Method.ContractType))
            .Accepts(route.Method.RequestType, options.DocumentationContentType);

        if (route.Method.Operation == ContractOperation.Request || route.Method.ExpectReply)
        {
            if (route.Method.ResponseType is null)
            {
                endpoint.Produces(StatusCodes.Status200OK);
            }
            else
            {
                endpoint.Produces(
                    StatusCodes.Status200OK,
                    route.Method.ResponseType,
                    options.DocumentationContentType);
            }
        }
        else
        {
            endpoint.Produces(StatusCodes.Status202Accepted);
        }

        endpoint
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
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

    private static string GetOperationRoute(
        RestRoute route,
        RestEndpointOptions options)
    {
        var method = route.Method;
        var implementationMethod = GetImplementationMethod(route);

        var contractRoute = ResolveRouteTemplate(
            "contract",
            GetCustomRouteCandidates(
                method.ContractType,
                "contract RestRouteAttribute"),
            GetFrameworkRouteCandidates(
                method.ContractType,
                "contract ASP.NET Core route"),
            GetFrameworkRouteCandidates(
                route.Registration.ImplementationType,
                "implementation ASP.NET Core route"));
        var methodRoute = ResolveRouteTemplate(
            "method",
            GetCustomRouteCandidates(method.Method, "contract method RestRouteAttribute"),
            GetCustomRouteCandidates(implementationMethod, "implementation method RestRouteAttribute"),
            GetFrameworkRouteCandidates(method.Method, "contract method ASP.NET Core route"),
            GetFrameworkRouteCandidates(implementationMethod, "implementation method ASP.NET Core route"));

        if (!string.IsNullOrWhiteSpace(methodRoute))
        {
            return CombineOperationRoutes(contractRoute, methodRoute);
        }

        if (!string.IsNullOrWhiteSpace(contractRoute))
        {
            return CombineOperationRoutes(
                contractRoute,
                RestEndpointOptions.GetOperationRouteSegment(method.MethodName));
        }

        var fallback = options.FallbackOperationRouteResolver?.Invoke(method);
        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new InvalidOperationException(
                $"No REST operation route is configured for '{method.ContractName}.{method.MethodName}'. " +
                "Use RestRouteAttribute, an ASP.NET Core route attribute, or configure " +
                "FallbackOperationRouteResolver.");
        }

        return NormalizeOperationRoute(fallback, "fallback operation route");
    }

    private static string GetOperationHttpMethod(
        RestRoute route,
        RestEndpointOptions options)
    {
        var implementationMethod = GetImplementationMethod(route);
        var candidates = GetCustomHttpMethodCandidates(
                route.Method.Method,
                "contract method RestHttpMethodAttribute")
            .Concat(GetCustomHttpMethodCandidates(
                implementationMethod,
                "implementation method RestHttpMethodAttribute"))
            .Concat(GetFrameworkHttpMethodCandidates(
                route.Method.Method,
                "contract method ASP.NET Core HTTP method"))
            .Concat(GetFrameworkHttpMethodCandidates(
                implementationMethod,
                "implementation method ASP.NET Core HTTP method"))
            .Select(candidate => new HttpMethodCandidate(
                NormalizeHttpMethod(candidate.Method, candidate.Source),
                candidate.Source))
            .DistinctBy(static candidate => candidate.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple HTTP methods are configured for " +
                $"'{route.Method.ContractName}.{route.Method.MethodName}': " +
                string.Join(
                    ", ",
                    candidates.Select(candidate =>
                        $"'{candidate.Method}' ({candidate.Source})")) + ". " +
                "Configure exactly one HTTP method for each AnyProtocol operation.");
        }

        if (candidates.Length == 1)
        {
            return candidates[0].Method;
        }

        var fallback = options.FallbackOperationHttpMethodResolver?.Invoke(route.Method);
        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new InvalidOperationException(
                $"No HTTP method is configured for " +
                $"'{route.Method.ContractName}.{route.Method.MethodName}'. " +
                "Use RestHttpMethodAttribute, an ASP.NET Core HTTP method attribute, " +
                "or configure FallbackOperationHttpMethodResolver.");
        }

        return NormalizeHttpMethod(fallback, "fallback HTTP method");
    }

    private static IEnumerable<HttpMethodCandidate> GetCustomHttpMethodCandidates(
        MemberInfo member,
        string source)
        => member.GetCustomAttributes<RestHttpMethodAttribute>(inherit: true)
            .Select(attribute => new HttpMethodCandidate(attribute.Method, source));

    private static IEnumerable<HttpMethodCandidate> GetFrameworkHttpMethodCandidates(
        MemberInfo member,
        string source)
        => member.GetCustomAttributes(inherit: true)
            .OfType<IActionHttpMethodProvider>()
            .SelectMany(provider => provider.HttpMethods.Select(
                method => new HttpMethodCandidate(method, source)));

    private static string NormalizeHttpMethod(string method, string source)
    {
        var normalized = method.Trim().ToUpperInvariant();
        if (normalized.Length == 0 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                !"!#$%&'*+-.^_`|~".Contains(character)))
        {
            throw new InvalidOperationException(
                $"The HTTP method '{method}' from {source} is not a valid HTTP method token.");
        }

        return normalized;
    }

    private static MethodInfo GetImplementationMethod(RestRoute route)
    {
        var map = route.Registration.ImplementationType.GetInterfaceMap(route.Method.ContractType);
        var methodIndex = Array.IndexOf(map.InterfaceMethods, route.Method.Method);
        return methodIndex >= 0 ? map.TargetMethods[methodIndex] : route.Method.Method;
    }

    private static IEnumerable<RouteCandidate> GetCustomRouteCandidates(
        MemberInfo member,
        string source)
        => member.GetCustomAttributes<RestRouteAttribute>(inherit: true)
            .Select(attribute => new RouteCandidate(attribute.Template, source));

    private static IEnumerable<RouteCandidate> GetFrameworkRouteCandidates(
        MemberInfo member,
        string source)
        => member.GetCustomAttributes(inherit: true)
            .OfType<IRouteTemplateProvider>()
            .Where(provider => provider.Template is not null)
            .Select(provider => new RouteCandidate(provider.Template!, source));

    private static string? ResolveRouteTemplate(
        string scope,
        params IEnumerable<RouteCandidate>[] candidateGroups)
    {
        var candidates = candidateGroups
            .SelectMany(static group => group)
            .Select(candidate =>
            {
                var normalized = NormalizeOperationRoute(
                    candidate.Template,
                    $"{scope} route from {candidate.Source}");
                return new RouteCandidate(normalized, candidate.Source);
            })
            .DistinctBy(static candidate => candidate.Template, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple {scope} REST routes are configured: " +
                string.Join(
                    ", ",
                    candidates.Select(candidate =>
                        $"'{candidate.Template}' ({candidate.Source})")) + ". " +
                "Configure exactly one route template for each AnyProtocol operation.");
        }

        return candidates.SingleOrDefault()?.Template;
    }


    private static string CombineOperationRoutes(string? prefix, string route)
    {
        var normalizedRoute = NormalizeOperationRoute(route, "operation route");
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return normalizedRoute;
        }

        return $"{NormalizeOperationRoute(prefix, "contract route prefix")}/{normalizedRoute}";
    }

    private static string NormalizeOperationRoute(string route, string description)
    {
        var normalized = route.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('{') ||
            normalized.Contains('}') ||
            normalized.Contains('[') ||
            normalized.Contains(']') ||
            normalized.Contains('?') ||
            normalized.Contains('#') ||
            normalized.Split('/').Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"The {description} '{route}' cannot be represented as a REST route.");
        }

        return normalized;
    }

    private static string GetOperationName(ContractMethodDescriptor method)
        => $"AnyProtocol_{ToIdentifier(method.ContractName)}_{ToIdentifier(method.MethodName)}";

    private static string GetContractTag(Type contractType)
        => contractType.Name.Length > 1 &&
           contractType.Name[0] == 'I' &&
           char.IsUpper(contractType.Name[1])
            ? contractType.Name[1..]
            : contractType.Name;

    private static string ToIdentifier(string value)
        => string.Concat(value.Select(
            static character => char.IsAsciiLetterOrDigit(character) ? character : '_'));

    private sealed record RouteCandidate(string Template, string Source);

    private sealed record HttpMethodCandidate(string Method, string Source);

    private sealed record RestRoute(
        ServerRegistration Registration,
        ContractMethodDescriptor Method,
        ProtocolKey Protocol);

    private sealed record RestEventRoute(EventRegistration Registration);

    private sealed class RestEndpointMarker;

    private sealed class CaptureProtocol : ISendTransport
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
