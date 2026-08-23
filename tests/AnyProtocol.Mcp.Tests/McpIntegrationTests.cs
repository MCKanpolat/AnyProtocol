using AnyProtocol.Abstraction;
using AnyProtocol.Authorization.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Mcp.AspNetCore;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Grpc.AspNetCore;
using AnyProtocol.Protocol.Rest.AspNetCore;
using AnyProtocol.Serializer.TextJson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Grpc.Net.Client;
using GrpcClientProtocol = AnyProtocol.Protocol.Grpc.GrpcMessagingProtocol;
using RestClientProtocol = AnyProtocol.Protocol.Rest.RestMessagingProtocol;

namespace AnyProtocol.Mcp.Tests;

public sealed record CalculationRequest(int Left, int Right);

public sealed record CalculationResponse(int Value, int Instance);

public interface ICalculationService
{
    [McpTool(
        Name = "calculator_add",
        Description = "Adds two integer values.",
        ReadOnly = true,
        Idempotent = true)]
    [RequirePermission("calculator.use")]
    ValueTask<CalculationResponse> AddAsync(CalculationRequest request);

    [McpTool(Name = "calculator_fail")]
    ValueTask<CalculationResponse> FailAsync(CalculationRequest request);

    [McpTool(Name = "calculator_wait")]
    ValueTask<CalculationResponse> WaitAsync(
        CalculationRequest request,
        CancellationToken cancellationToken);

    [McpTool(Name = "calculator_retryable")]
    ValueTask<CalculationResponse> RetryableAsync(CalculationRequest request);

    ValueTask<CalculationResponse> HiddenAsync(CalculationRequest request);
}

public sealed class CalculationService : ICalculationService
{
    private static int _instances;
    private readonly int _instance = Interlocked.Increment(ref _instances);

    public ValueTask<CalculationResponse> AddAsync(CalculationRequest request)
        => ValueTask.FromResult(
            new CalculationResponse(request.Left + request.Right, _instance));

    public ValueTask<CalculationResponse> FailAsync(CalculationRequest request)
        => throw new InvalidOperationException("Calculation failed safely.");

    public async ValueTask<CalculationResponse> WaitAsync(
        CalculationRequest request,
        CancellationToken cancellationToken)
    {
        CancellationProbe.Entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new CalculationResponse(0, _instance);
    }

    public ValueTask<CalculationResponse> RetryableAsync(CalculationRequest request)
        => throw new AnyProtocolFaultException(
            new FaultMessage(
                "calculation_retryable",
                "Calculation can be retried.",
                Retryable: true,
                Details: [new FaultDetail("operation", "retry")]));

    public ValueTask<CalculationResponse> HiddenAsync(CalculationRequest request)
        => ValueTask.FromResult(new CalculationResponse(0, _instance));
}

public sealed class McpIntegrationTests
{
    [Fact]
    public async Task Host_start_rejects_mcp_server_without_mcp_adapter_registration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ConfigureAnyProtocol(builder.Services);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.StartAsync());

        Assert.Contains("MCP", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AddAnyProtocolMcp", exception.Message);
    }

    [Fact]
    public async Task Host_start_rejects_mcp_http_server_without_endpoint_mapping()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        ConfigureAnyProtocol(builder.Services);
        builder.Services.AddAnyProtocolMcp();
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.StartAsync());

        Assert.Contains("MCP", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MapAnyProtocolMcp", exception.Message);
    }

    [Fact]
    public async Task Http_mcp_mapping_requires_authorization_unless_the_development_opt_out_is_selected()
    {
        var securedBuilder = WebApplication.CreateBuilder();
        securedBuilder.WebHost.UseTestServer();
        securedBuilder.Services
            .AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        securedBuilder.Services.AddAuthorization(options => options.AddPolicy(
            "administrator",
            policy => policy.RequireClaim("role", "administrator")));
        ConfigureAnyProtocol(securedBuilder.Services);
        securedBuilder.Services.AddAnyProtocolMcp();
        await using var secured = securedBuilder.Build();
        secured.UseAuthentication();
        secured.UseAuthorization();
        secured.MapAnyProtocolMcp("/mcp");
        secured.MapAnyProtocolMcp("/mcp-admin").RequireAuthorization("administrator");

        var developmentBuilder = WebApplication.CreateBuilder();
        developmentBuilder.WebHost.UseTestServer();
        ConfigureAnyProtocol(developmentBuilder.Services);
        developmentBuilder.Services.AddAnyProtocolMcp();
        await using var development = developmentBuilder.Build();
        development.MapAnyProtocolMcpAllowAnonymousForDevelopment("/mcp");

        await secured.StartAsync();
        await development.StartAsync();

        var securedEndpoints = secured.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var developmentEndpoints = development.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Contains(
            securedEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null);
        Assert.DoesNotContain(
            developmentEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null);

        var securedClient = secured.GetTestClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await securedClient.PostAsync("/mcp", content: null)).StatusCode);
        securedClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("test", "authenticated");
        Assert.NotEqual(
            HttpStatusCode.Unauthorized,
            (await securedClient.PostAsync("/mcp", content: null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await securedClient.PostAsync("/mcp-admin", content: null)).StatusCode);
        Assert.NotEqual(
            HttpStatusCode.Unauthorized,
            (await development.GetTestClient().PostAsync("/mcp", content: null)).StatusCode);
    }

    [Fact]
    public async Task One_server_registration_is_exposed_over_rest_grpc_and_mcp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAuthorizationProvider>(
            new TestAuthorizationProvider { IsAllowed = true });
        builder.Services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport(ProtocolKey.Rest, new RestServerProtocol())
            .AddTransport(ProtocolKey.Grpc, new GrpcServerProtocol())
            .AddServerFilter(new AuthorizationFilter())
            .AddServer<ICalculationService, CalculationService>(server => server.UseProtocols(
                ProtocolKey.Rest,
                ProtocolKey.Grpc,
                ProtocolKey.Mcp)));
        builder.Services.AddAnyProtocolRest();
        builder.Services.AddAnyProtocolGrpc();
        builder.Services.AddAnyProtocolMcp();
        await using var app = builder.Build();
        app.MapAnyProtocol();
        app.MapAnyProtocolGrpc();
        app.MapAnyProtocolMcpAllowAnonymousForDevelopment("/mcp");
        await app.StartAsync();

        var serializer = new TextJsonMessageSerializer();
        await using var restClient = CreateProtocolClient(
            ProtocolKey.Rest,
            new RestClientProtocol(app.GetTestClient(), serializer),
            serializer);
        var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = app.GetTestServer().CreateHandler() });
        await using var grpcClient = CreateProtocolClient(
            ProtocolKey.Grpc,
            new GrpcClientProtocol(channel, disposeChannel: true),
            serializer);

        var restResult = await restClient.GetRequiredService<ICalculationService>()
            .AddAsync(new CalculationRequest(20, 22));
        var grpcResult = await grpcClient.GetRequiredService<ICalculationService>()
            .AddAsync(new CalculationRequest(19, 23));
        var tools = app.Services.GetRequiredService<McpToolCatalog>().Tools;

        Assert.Equal(42, restResult.Value);
        Assert.Equal(42, grpcResult.Value);
        Assert.Contains(tools, tool => tool.Name == "calculator_add");
        Assert.Single(app.Services.GetRequiredService<RuntimePlan>().ServerRegistrations);
    }

    [Fact]
    public void Catalog_exposes_only_opted_in_tools_with_json_schema()
    {
        using var provider = CreateServices().BuildServiceProvider();

        var catalog = provider.GetRequiredService<McpToolCatalog>();

        Assert.Equal(
            ["calculator_add", "calculator_fail", "calculator_retryable", "calculator_wait"],
            catalog.Tools.Select(
            static tool => tool.Name).Order());
        var add = catalog.GetRequired("calculator_add");
        Assert.True(add.ReadOnly);
        Assert.True(add.Idempotent);
        Assert.Equal("object", add.InputSchema.GetProperty("type").GetString());
        Assert.True(add.InputSchema.GetProperty("properties").TryGetProperty("left", out _));
        Assert.Throws<KeyNotFoundException>(() => catalog.GetRequired("HiddenAsync"));
    }

    [Fact]
    public void Catalog_uses_the_compiled_runtime_plan_binding()
    {
        using var provider = CreateServices().BuildServiceProvider();
        var plan = provider.GetRequiredService<RuntimePlan>();
        var binding = Assert.Single(
            plan.McpToolPlans.Where(static candidate => candidate.Name == "calculator_add"));
        var tool = provider.GetRequiredService<McpToolCatalog>().GetRequired("calculator_add");

        Assert.Same(binding.Method, tool.Method);
        Assert.Same(binding.Registration, tool.Registration);
        Assert.True(tool.Idempotent);
    }

    [Fact]
    public async Task Tool_invocation_enforces_permissions()
    {
        var authorization = new TestAuthorizationProvider { IsAllowed = false };
        await using var provider = CreateServices(authorization: authorization)
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<McpToolInvoker>();

        var denied = await invoker.InvokeAsync("calculator_add", CreateArguments());
        authorization.IsAllowed = true;
        var allowed = await invoker.InvokeAsync("calculator_add", CreateArguments());

        Assert.True(denied.IsError);
        Assert.Equal("permission_denied", denied.ErrorCode);
        Assert.NotNull(denied.Error);
        Assert.False(denied.Error.Retryable);
        Assert.False(allowed.IsError);
        Assert.Equal(["calculator.use"], authorization.LastPermissions);
    }

    [Fact]
    public async Task Bearer_credential_is_propagated_to_dispatcher_headers()
    {
        var capture = new CaptureAuthTokenFilter();
        await using var provider = CreateServices(serverFilter: capture)
            .AddSingleton<IMcpCredentialProvider>(
                new StaticCredentialProvider("sample-token"))
            .BuildServiceProvider();

        await provider.GetRequiredService<McpToolInvoker>()
            .InvokeAsync("calculator_add", CreateArguments());

        Assert.Equal("sample-token", capture.Token);
    }

    [Fact]
    public async Task Tool_cancellation_reaches_handler()
    {
        CancellationProbe.Reset();
        await using var provider = CreateServices().BuildServiceProvider();
        var invoker = provider.GetRequiredService<McpToolInvoker>();
        using var cancellation = new CancellationTokenSource();

        var invocation = invoker.InvokeAsync(
                "calculator_wait",
                CreateArguments(),
                cancellation.Token)
            .AsTask();
        await CancellationProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
    }

    [Fact]
    public void Catalog_rejects_duplicate_tool_names_and_streaming_tools()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreateCatalog<IDuplicateToolService, DuplicateToolService>());
        Assert.Throws<InvalidOperationException>(
            () => CreateCatalog<IStreamingToolService, StreamingToolService>());
    }

    [Fact]
    public async Task Tool_invocation_uses_dispatcher_and_maps_faults()
    {
        await using var provider = CreateServices().BuildServiceProvider();
        var invoker = provider.GetRequiredService<McpToolInvoker>();
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["left"] = System.Text.Json.JsonSerializer.SerializeToElement(12),
            ["right"] = System.Text.Json.JsonSerializer.SerializeToElement(30)
        };

        var success = await invoker.InvokeAsync("calculator_add", arguments);
        var secondSuccess = await invoker.InvokeAsync("calculator_add", arguments);
        var failure = await invoker.InvokeAsync("calculator_fail", arguments);
        var retryableFailure = await invoker.InvokeAsync("calculator_retryable", arguments);

        Assert.False(success.IsError);
        Assert.Equal(
            42,
            success.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.NotEqual(
            success.StructuredContent.Value.GetProperty("instance").GetInt32(),
            secondSuccess.StructuredContent!.Value.GetProperty("instance").GetInt32());
        Assert.True(failure.IsError);
        Assert.Equal("handler_failed", failure.ErrorCode);
        Assert.NotNull(failure.Error);
        Assert.Empty(failure.Error.Details);
        Assert.DoesNotContain("System.", failure.Message, StringComparison.Ordinal);
        Assert.True(retryableFailure.IsError);
        Assert.Equal("calculation_retryable", retryableFailure.ErrorCode);
        Assert.NotNull(retryableFailure.Error);
        Assert.True(retryableFailure.Error.Retryable);
        Assert.Equal([new FaultDetail("operation", "retry")], retryableFailure.Error.Details);
    }

    [Fact]
    public async Task Streamable_http_client_discovers_and_invokes_tools()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        ConfigureAnyProtocol(builder.Services, useRest: true);
        builder.Services.AddAnyProtocolRest();
        builder.Services.AddAnyProtocolMcp();
        var app = builder.Build();
        app.MapGet("/health", () => "ok");
        app.MapAnyProtocol("/api");
        app.MapAnyProtocolMcpAllowAnonymousForDevelopment("/mcp");
        await app.StartAsync();

        try
        {
            var httpClient = app.GetTestClient();
            Assert.Equal("ok", await httpClient.GetStringAsync("/health"));
            var restResponse = await httpClient.PostAsync(
                "/api/anyprotocol.calculation-service.add",
                new StringContent(
                    """{"Left":19,"Right":23}""",
                    Encoding.UTF8,
                    "application/json"));
            restResponse.EnsureSuccessStatusCode();
            Assert.Equal(
                42,
                (await restResponse.Content.ReadFromJsonAsync<CalculationResponse>())!.Value);
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    Name = "AnyProtocol tests"
                },
                httpClient);
            await using var client = await McpClient.CreateAsync(transport);

            var tools = await client.ListToolsAsync();
            var result = await client.CallToolAsync(
                "calculator_add",
                new Dictionary<string, object?> { ["left"] = 20, ["right"] = 22 });

            Assert.Equal(
                ["calculator_add", "calculator_fail", "calculator_retryable", "calculator_wait"],
                tools.Select(static tool => tool.Name).Order());
            Assert.False(result.IsError);
            Assert.Equal(
                42,
                result.StructuredContent!.Value.GetProperty("value").GetInt32());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Stdio_client_discovers_and_invokes_sample_host()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hostAssembly = Path.Combine(
            repositoryRoot,
            "samples",
            "OrderSystem.McpHost",
            "bin",
            configuration,
            "net10.0",
            "OrderSystem.McpHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Stdio host not found at '{hostAssembly}'.");
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [hostAssembly, "--stdio"],
                WorkingDirectory = repositoryRoot,
                Name = "AnyProtocol stdio tests"
            });
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync(
            "orders_get",
            new Dictionary<string, object?> { ["orderId"] = "ORD-42" });

        Assert.Equal(["orders_get"], tools.Select(static tool => tool.Name));
        Assert.False(result.IsError);
        Assert.Equal(
            "ORD-42",
            result.StructuredContent!.Value.GetProperty("orderId").GetString());
    }

    private static ServiceCollection CreateServices(
        TestAuthorizationProvider? authorization = null,
        IMessageFilter? serverFilter = null)
    {
        var services = new ServiceCollection();
        authorization ??= new TestAuthorizationProvider { IsAllowed = true };
        services.AddSingleton<IAuthorizationProvider>(authorization);
        ConfigureAnyProtocol(services, serverFilter: serverFilter);
        services.AddAnyProtocolMcp();
        return services;
    }

    private static void ConfigureAnyProtocol(
        IServiceCollection services,
        bool useRest = false,
        IMessageFilter? serverFilter = null)
    {
        services.TryAddSingleton<IAuthorizationProvider>(
            new TestAuthorizationProvider { IsAllowed = true });
        services.AddAnyProtocol(
            link =>
            {
                link.UseSerializer(new TextJsonMessageSerializer());
                if (useRest)
                {
                    link.AddRestServer("transport");
                }
                else
                {
                    link.AddTransport("transport", new InMemoryMessagingProtocol());
                }

                link.AddServerFilter(new AuthorizationFilter());
                if (serverFilter is not null)
                {
                    link.AddServerFilter(serverFilter);
                }

                link.AddServer<ICalculationService, CalculationService>(
                    server => server.UseProtocols(
                        ProtocolKey.Create("transport"),
                        ProtocolKey.Mcp));
            });
    }

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> CreateArguments()
        => new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["left"] = System.Text.Json.JsonSerializer.SerializeToElement(20),
            ["right"] = System.Text.Json.JsonSerializer.SerializeToElement(22)
        };

    private static ServiceProvider CreateProtocolClient(
        ProtocolKey protocol,
        IMessagingProtocol transport,
        TextJsonMessageSerializer serializer)
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(serializer)
            .AddTransport(protocol, transport)
            .AddClient<ICalculationService>(client => client.UseProtocol(protocol)));
        return services.BuildServiceProvider();
    }

    private static McpToolCatalog CreateCatalog<TContract, TImplementation>()
        where TContract : class
        where TImplementation : class, TContract
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(
            link =>
            {
                link.UseSerializer(new TextJsonMessageSerializer());
                link.AddTransport("transport", new InMemoryMessagingProtocol());
                link.AddServer<TContract, TImplementation>(
                    server => server.UseProtocols(
                        ProtocolKey.Create("transport"),
                        ProtocolKey.Mcp));
            });
        services.AddAnyProtocolMcp();
        return services.BuildServiceProvider().GetRequiredService<McpToolCatalog>();
    }
}

public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Name, "test-user"));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
    }
}

public sealed class TestAuthorizationProvider : IAuthorizationProvider
{
    public bool IsAllowed { get; set; }

    public string[] LastPermissions { get; private set; } = [];

    public ValueTask<bool> CheckPermissionsAsync(
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default)
    {
        LastPermissions = permissions.ToArray();
        return ValueTask.FromResult(IsAllowed);
    }
}

public sealed class CaptureAuthTokenFilter : IMessageFilter
{
    public string? Token { get; private set; }

    public async ValueTask InvokeAsync(
        IMessageContext context,
        MessageFilterDelegate next)
    {
        Token = context.Headers.TryGetValue(HeaderNames.AuthToken, out var token)
            ? token
            : null;
        await next(context);
    }
}

public sealed class StaticCredentialProvider(string token) : IMcpCredentialProvider
{
    public string? GetAuthToken() => token;
}

internal static class CancellationProbe
{
    public static TaskCompletionSource Entered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
        => Entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
}

public interface IDuplicateToolService
{
    [McpTool(Name = "duplicate")]
    ValueTask<CalculationResponse> FirstAsync(CalculationRequest request);

    [McpTool(Name = "duplicate")]
    ValueTask<CalculationResponse> SecondAsync(CalculationRequest request);
}

public sealed class DuplicateToolService : IDuplicateToolService
{
    public ValueTask<CalculationResponse> FirstAsync(CalculationRequest request)
        => ValueTask.FromResult(new CalculationResponse(1, 1));

    public ValueTask<CalculationResponse> SecondAsync(CalculationRequest request)
        => ValueTask.FromResult(new CalculationResponse(2, 1));
}

public interface IStreamingToolService
{
    [McpTool(Name = "streaming")]
    IAsyncEnumerable<CalculationResponse> StreamAsync(CalculationRequest request);
}

public sealed class StreamingToolService : IStreamingToolService
{
    public async IAsyncEnumerable<CalculationResponse> StreamAsync(
        CalculationRequest request)
    {
        yield return new CalculationResponse(request.Left, 1);
        await Task.CompletedTask;
    }
}
