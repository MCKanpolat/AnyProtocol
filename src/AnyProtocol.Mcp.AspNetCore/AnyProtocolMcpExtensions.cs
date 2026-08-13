using System.Text.Json;
using AnyProtocol.Configuration;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;

namespace AnyProtocol.Mcp.AspNetCore;

/// <summary>
/// Provides extension methods for anyprotocol mcp configuration and registration.
/// </summary>
public static class AnyProtocolMcpExtensions
{
    /// <summary>
    /// Adds anyprotocol mcp support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add anyprotocol mcp operation.</returns>
    public static IServiceCollection AddAnyProtocolMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterAnyProtocolMcpServices(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProtocolExposureDeclaration, McpHttpExposureDeclaration>());
        ConfigureHandlers(services.AddMcpServer().WithHttpTransport());
        return services;
    }

    /// <summary>
    /// Adds anyprotocol mcp stdio support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add anyprotocol mcp stdio operation.</returns>
    public static IServiceCollection AddAnyProtocolMcpStdio(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterAnyProtocolMcpServices(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProtocolExposureDeclaration, McpStdioExposureDeclaration>());
        ConfigureHandlers(services.AddMcpServer().WithStdioServerTransport());
        return services;
    }

    /// <summary>
    /// Maps anyprotocol mcp endpoints into the application pipeline.
    /// </summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The result of the map anyprotocol mcp operation.</returns>
    public static IEndpointConventionBuilder MapAnyProtocolMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var mappedEndpoint = endpoints.MapMcp(pattern);
        endpoints.ServiceProvider
            .GetRequiredService<ProtocolExposureRegistry>()
            .MarkMapped(ProtocolKey.Mcp);
        return mappedEndpoint;
    }

    private static void RegisterAnyProtocolMcpServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IMcpCredentialProvider, HttpContextMcpCredentialProvider>();
        services.TryAddSingleton<McpToolCatalog>();
        services.TryAddSingleton<McpToolInvoker>();
        services.TryAddSingleton<ProtocolExposureRegistry>();
        services.AddHostedService<McpCatalogValidationService>();
    }

    private static void ConfigureHandlers(IMcpServerBuilder builder)
    {
        builder
            .WithListToolsHandler(
                (request, _) =>
                {
                    var services = request.Services ??
                                   throw new InvalidOperationException(
                                       "The MCP request has no service provider.");
                    var catalog = services.GetRequiredService<McpToolCatalog>();
                    return ValueTask.FromResult(
                        new ListToolsResult
                        {
                            Tools = catalog.Tools.Select(
                                    static tool => new Tool
                                    {
                                        Name = tool.Name,
                                        Description = tool.Description,
                                        InputSchema = tool.InputSchema,
                                        OutputSchema = tool.OutputSchema,
                                        Annotations = new ToolAnnotations
                                        {
                                            ReadOnlyHint = tool.ReadOnly,
                                            DestructiveHint = tool.Destructive,
                                            IdempotentHint = tool.Idempotent,
                                            OpenWorldHint = tool.OpenWorld
                                        }
                                    })
                                .ToArray()
                        });
                })
            .WithCallToolHandler(
                async (request, cancellationToken) =>
                {
                    var parameters = request.Params ??
                                     throw new InvalidOperationException(
                                         "MCP tool call parameters are required.");
                    var arguments = parameters.Arguments is null
                        ? null
                        : new Dictionary<string, JsonElement>(parameters.Arguments);
                    var services = request.Services ??
                                   throw new InvalidOperationException(
                                       "The MCP request has no service provider.");
                    var invoker = services.GetRequiredService<McpToolInvoker>();
                    var result = await invoker.InvokeAsync(
                            parameters.Name,
                            arguments,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var content = result.IsError
                        ? JsonSerializer.SerializeToElement(
                            new { error = result.Error },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        : result.StructuredContent;
                    var text = result.IsError
                        ? $"{result.ErrorCode}: {result.Message}"
                        : content?.GetRawText() ?? "{}";
                    return new CallToolResult
                    {
                        IsError = result.IsError,
                        StructuredContent = content,
                        Content = [new TextContentBlock { Text = text }]
                    };
                });
    }

    private sealed class McpHttpExposureDeclaration : IProtocolExposureDeclaration
    {
        public ProtocolKey Protocol => ProtocolKey.Mcp;

        public bool RequiresEndpointMapping => true;
    }

    private sealed class McpStdioExposureDeclaration : IProtocolExposureDeclaration
    {
        public ProtocolKey Protocol => ProtocolKey.Mcp;

        public bool RequiresEndpointMapping => false;
    }

    private sealed class HttpContextMcpCredentialProvider(
        IHttpContextAccessor httpContextAccessor) : IMcpCredentialProvider
    {
        public string? GetAuthToken()
        {
            var authorization = httpContextAccessor.HttpContext?
                .Request.Headers.Authorization.ToString();
            return authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authorization["Bearer ".Length..].Trim()
                : null;
        }
    }
}
