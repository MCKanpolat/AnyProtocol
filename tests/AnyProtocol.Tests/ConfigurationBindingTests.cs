using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class ConfigurationBindingTests
{
    [Fact]
    public void External_configuration_overrides_named_client_and_server_protocols()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AnyProtocol:Clients:OrdersClient:Protocol"] = "secondary",
                ["AnyProtocol:Servers:OrdersServer:Protocols:0"] = "primary",
                ["AnyProtocol:Servers:OrdersServer:Protocols:1"] = "secondary"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAnyProtocol(
            configuration.GetSection("AnyProtocol"),
            link => link
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(ProtocolKey.Create("primary"), new InMemoryMessagingProtocol())
                .AddTransport(ProtocolKey.Create("secondary"), new InMemoryMessagingProtocol())
                .AddClient<IConfiguredService>(
                    "OrdersClient",
                    client => client.UseProtocol(ProtocolKey.Create("primary")))
                .AddServer<IConfiguredService, ConfiguredService>(
                    "OrdersServer",
                    server => server.UseProtocols(ProtocolKey.Create("primary"))));

        using var provider = services.BuildServiceProvider();
        var link = provider.GetRequiredService<AnyProtocol.Configuration.RuntimePlan>();

        Assert.Equal(
            ProtocolKey.Create("secondary"),
            Assert.Single(link.ClientRegistrations).Protocol);
        Assert.Equal(
            [ProtocolKey.Create("primary"), ProtocolKey.Create("secondary")],
            Assert.Single(link.ServerRegistrations).Protocols);
    }

    [Fact]
    public void External_configuration_rejects_unknown_registration_keys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AnyProtocol:Clients:Typo:Protocol"] = "primary"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAnyProtocol(
                configuration.GetSection("AnyProtocol"),
                link => link
                    .UseSerializer(new TextJsonMessageSerializer())
                    .AddTransport(
                        ProtocolKey.Create("primary"),
                        new InMemoryMessagingProtocol())
                    .AddClient<IConfiguredService>("OrdersClient")));

        Assert.Contains("Typo", exception.Message);
        Assert.Contains("client", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void External_configuration_rejects_empty_server_protocol_lists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AnyProtocol:Servers:OrdersServer:Protocols"] = string.Empty
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddAnyProtocol(
                configuration.GetSection("AnyProtocol"),
                link => link
                    .UseSerializer(new TextJsonMessageSerializer())
                    .AddTransport(
                        ProtocolKey.Create("primary"),
                        new InMemoryMessagingProtocol())
                    .AddServer<IConfiguredService, ConfiguredService>("OrdersServer")));

        Assert.Contains("at least one protocol", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public interface IConfiguredService
{
    ValueTask<string> GetAsync(CancellationToken cancellationToken);
}

public sealed class ConfiguredService : IConfiguredService
{
    public ValueTask<string> GetAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult("configured");
}
