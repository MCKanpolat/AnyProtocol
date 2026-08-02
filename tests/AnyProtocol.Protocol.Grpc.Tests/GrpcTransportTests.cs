using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Grpc.AspNetCore;
using AnyProtocol.Serializer.TextJson;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AnyProtocol.Protocol.Grpc.Tests;

public sealed record GrpcRequest(string Value, int Count = 0);

public sealed record GrpcResponse(string Value);

public interface IGrpcTestService
{
    ValueTask<GrpcResponse> EchoAsync(
        GrpcRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<GrpcResponse> StreamAsync(
        GrpcRequest request,
        CancellationToken cancellationToken);
}

public sealed class StreamScopeProbe : IDisposable
{
    public static TaskCompletionSource Disposed { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        Disposed.TrySetResult();
    }
}

public sealed class GrpcTestService(StreamScopeProbe scopeProbe) : IGrpcTestService
{
    public static TaskCompletionSource CancellationObserved { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<GrpcResponse> EchoAsync(
        GrpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Value == "slow")
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        if (request.Value == "fail")
        {
            throw new InvalidOperationException("Unary failure.");
        }

        return new GrpcResponse(request.Value);
    }

    public async IAsyncEnumerable<GrpcResponse> StreamAsync(
        GrpcRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            for (var index = 0; index < request.Count; index++)
            {
                ObjectDisposedException.ThrowIf(scopeProbe.IsDisposed, scopeProbe);
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Value == "fail" && index == 2)
                {
                    throw new InvalidOperationException("Stream failure.");
                }

                yield return new GrpcResponse($"{request.Value}-{index}");
                await Task.Delay(10, cancellationToken);
            }

            if (request.Value == "infinite")
            {
                var index = 0;
                while (true)
                {
                    yield return new GrpcResponse($"{request.Value}-{index++}");
                    await Task.Delay(10, cancellationToken);
                }
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
            }
        }
    }
}

public sealed class GrpcTransportTests
{
    [Fact]
    public void Configuration_rejects_two_grpc_server_protocols_for_one_contract()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new Configuration.LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddGrpcServer(ProtocolKey.Create("internal-grpc"))
                .AddGrpcServer(ProtocolKey.Create("external-grpc"))
                .AddServer<IGrpcTestService, GrpcTestService>(server => server.UseProtocols(
                    ProtocolKey.Create("internal-grpc"),
                    ProtocolKey.Create("external-grpc")))
                .Build());

        Assert.Contains("native server", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal-grpc", exception.Message);
        Assert.Contains("external-grpc", exception.Message);
    }

    [Fact]
    public async Task Server_readiness_requires_endpoint_mapping()
    {
        var protocol = new GrpcServerProtocol();

        var readiness = await protocol.CheckReadinessAsync();

        Assert.Equal(TransportReadinessState.NotReady, readiness.State);
    }

    [Fact]
    public async Task Unary_proxy_round_trips_and_coexists_with_regular_endpoints()
    {
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host);
        var client = clientProvider.GetRequiredService<IGrpcTestService>();

        var response = await client.EchoAsync(new GrpcRequest("hello"), CancellationToken.None);
        var health = await host.GetTestClient().GetStringAsync("/health");

        Assert.Equal("hello", response.Value);
        Assert.Equal("ok", health);
        var serverReadiness = await ((ITransportReadiness)host.Services
                .GetRequiredService<TransportRegistry>()
                .GetRequired("grpc"))
            .CheckReadinessAsync();
        Assert.Equal(TransportReadinessState.Ready, serverReadiness.State);
    }

    [Fact]
    public async Task Unary_fault_and_deadline_map_to_AnyProtocol_behavior()
    {
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host, TimeSpan.FromMilliseconds(100));
        var client = clientProvider.GetRequiredService<IGrpcTestService>();

        var fault = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await client.EchoAsync(
                new GrpcRequest("fail"),
                CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(
            async () => await client.EchoAsync(
                new GrpcRequest("slow"),
                CancellationToken.None));

        Assert.Equal("handler_failed", fault.Fault.Code);
    }

    [Fact]
    public async Task Server_stream_preserves_order_and_maps_mid_stream_fault()
    {
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host);
        var client = clientProvider.GetRequiredService<IGrpcTestService>();
        var values = new List<string>();

        await foreach (var item in client.StreamAsync(
                           new GrpcRequest("item", 5),
                           CancellationToken.None))
        {
            values.Add(item.Value);
        }

        var partial = new List<string>();
        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () =>
            {
                await foreach (var item in client.StreamAsync(
                                   new GrpcRequest("fail", 5),
                                   CancellationToken.None))
                {
                    partial.Add(item.Value);
                }
            });

        Assert.Equal(
            Enumerable.Range(0, 5).Select(index => $"item-{index}"),
            values);
        Assert.Equal(["fail-0", "fail-1"], partial);
        Assert.Equal("handler_failed", exception.Fault.Code);
    }

    [Fact]
    public async Task Client_stream_cancellation_reaches_handler()
    {
        GrpcTestService.CancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host);
        var client = clientProvider.GetRequiredService<IGrpcTestService>();
        using var cancellation = new CancellationTokenSource();

        await foreach (var _ in client.StreamAsync(
                           new GrpcRequest("infinite"),
                           cancellation.Token))
        {
            cancellation.Cancel();
            break;
        }

        await GrpcTestService.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Stream_deadline_maps_to_timeout_and_cancels_handler()
    {
        GrpcTestService.CancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(
            host,
            TimeSpan.FromMilliseconds(100));
        var client = clientProvider.GetRequiredService<IGrpcTestService>();

        await Assert.ThrowsAsync<TimeoutException>(
            async () =>
            {
                await foreach (var _ in client.StreamAsync(
                                   new GrpcRequest("infinite"),
                                   CancellationToken.None))
                {
                }
            });

        await GrpcTestService.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Streaming_scope_remains_alive_until_enumeration_completes()
    {
        StreamScopeProbe.Disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host);
        var client = clientProvider.GetRequiredService<IGrpcTestService>();

        await foreach (var _ in client.StreamAsync(
                           new GrpcRequest("scope", 3),
                           CancellationToken.None))
        {
            Assert.False(StreamScopeProbe.Disposed.Task.IsCompleted);
        }

        await StreamScopeProbe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<StreamScopeProbe>();
        builder.Services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddGrpcServer()
            .AddServer<IGrpcTestService, GrpcTestService>(
                server => server.UseTransport("grpc")));
        builder.Services.AddAnyProtocolGrpc();

        var app = builder.Build();
        app.MapGet("/health", () => "ok");
        app.MapAnyProtocolGrpc();
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider CreateClientProvider(
        WebApplication host,
        TimeSpan? timeout = null)
    {
        var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var transport = new GrpcMessagingProtocol(channel, disposeChannel: true);
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("grpc", transport)
            .AddClient<IGrpcTestService>(
                client => client
                    .UseTransport("grpc")
                    .WithTimeout(timeout ?? TimeSpan.FromSeconds(5))));
        return services.BuildServiceProvider();
    }
}
