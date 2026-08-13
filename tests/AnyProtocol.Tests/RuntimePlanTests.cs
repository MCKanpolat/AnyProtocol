using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class RuntimePlanTests
{
    [Fact]
    public void Build_returns_an_independent_immutable_snapshot()
    {
        var builder = new LinkBuilder()
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("primary", new InMemoryMessagingProtocol())
            .AddClient<IPlanContract>("plan-client", options => options.UseTransport("primary"))
            .AddServer<IPlanContract, PlanService>(
                "plan-server",
                options => options.UseTransport("primary"));

        var first = builder.Build();

        builder
            .AddTransport("secondary", new InMemoryMessagingProtocol())
            .ConfigureClientProtocol("plan-client", AnyProtocol.Protocol.Abstraction.ProtocolKey.Create("secondary"))
            .ConfigureServerProtocols(
                "plan-server",
                AnyProtocol.Protocol.Abstraction.ProtocolKey.Create("secondary"));
        var second = builder.Build();

        Assert.Single(first.RegisteredTransports);
        Assert.Equal("primary", first.ClientRegistrations.Single().TransportName);
        Assert.Equal("primary", first.ServerRegistrations.Single().TransportName);
        Assert.Equal(2, second.RegisteredTransports.Count);
        Assert.Equal("secondary", second.ClientRegistrations.Single().TransportName);
        Assert.Equal("secondary", second.ServerRegistrations.Single().TransportName);
        Assert.True(first.ClientsByContract.ContainsKey(typeof(IPlanContract)));
        Assert.NotEmpty(first.ServerRoutes);
        Assert.False((object)first.ClientRegistrations is List<ClientRegistration>);
    }

    [Fact]
    public void Build_rejects_duplicate_client_contracts()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("memory", new InMemoryMessagingProtocol())
                .AddClient<IPlanContract>("first", options => options.UseTransport("memory"))
                .AddClient<IPlanContract>("second", options => options.UseTransport("memory"))
                .Build());

        Assert.Contains(typeof(IPlanContract).FullName!, exception.Message);
        Assert.Contains("exactly once", exception.Message);
    }

    public interface IPlanContract
    {
        ValueTask<PlanResponse> GetAsync(PlanRequest request, CancellationToken cancellationToken);
    }

    public sealed record PlanRequest(string Value);

    public sealed record PlanResponse(string Value);

    public sealed class PlanService : IPlanContract
    {
        public ValueTask<PlanResponse> GetAsync(PlanRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new PlanResponse(request.Value));
    }
}
