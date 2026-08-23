using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Rest;

namespace AnyProtocol.Protocol.Rest.AspNetCore;

/// <summary>
/// Controls the ASP.NET Core REST endpoints exposed by AnyProtocol.
/// </summary>
public sealed class RestEndpointOptions
{
    /// <summary>
    /// Gets or sets the maximum number of request-body bytes accepted by an endpoint.
    /// </summary>
    /// <value>The limit in bytes. The default is 10 MiB.</value>
    /// <remarks>
    /// Configure matching limits in Kestrel, IIS, reverse proxies, and ingress so the
    /// earliest component rejects oversized requests.
    /// </remarks>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets or sets a value indicating whether operation-specific routes are mapped.
    /// </summary>
    /// <value>true to map operation-specific routes; otherwise, false.</value>
    public bool MapOperationEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the content type used by endpoint metadata for documented payloads.
    /// </summary>
    /// <value>The documented payload content type.</value>
    public string DocumentationContentType { get; set; } = "application/json";

    /// <summary>
    /// Gets or sets the fallback operation route resolver.
    /// </summary>
    /// <value>
    /// A resolver that returns a route relative to the <c>MapAnyProtocol</c> prefix
    /// when no explicit <see cref="RestRouteAttribute"/> or ASP.NET Core route
    /// metadata is present.
    /// </value>
    public Func<ContractMethodDescriptor, string?>? FallbackOperationRouteResolver { get; set; } =
        DefaultFallbackOperationRoute;

    /// <summary>
    /// Gets or sets the fallback HTTP method resolver for operation endpoints.
    /// </summary>
    /// <value>
    /// A resolver that returns an HTTP method when no explicit REST or ASP.NET Core
    /// HTTP method metadata is present. The default is <c>POST</c>.
    /// </value>
    public Func<ContractMethodDescriptor, string?>? FallbackOperationHttpMethodResolver { get; set; } =
        static _ => "POST";

    internal static string DefaultFallbackOperationRoute(ContractMethodDescriptor method)
        => $"{method.Channel}/{GetOperationRouteSegment(method.MethodName)}";

    internal static string GetOperationRouteSegment(string methodName)
    {
        var routeName = methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName[..^5]
            : methodName;
        return ToKebabCase(routeName);
    }

    private static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsAsciiLetterOrDigit(character))
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                continue;
            }

            if (char.IsUpper(character) &&
                builder.Length > 0 &&
                builder[^1] != '-' &&
                (char.IsLower(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString().Trim('-');
    }
}
