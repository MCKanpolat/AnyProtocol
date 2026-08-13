using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class TransportInterfaceTests
{
    [Fact]
    public void Common_transport_contract_does_not_expose_operation_members()
    {
        Assert.Null(typeof(IMessagingProtocol).GetMethod(nameof(ISendTransport.SendAsync)));
        Assert.Null(typeof(IMessagingProtocol).GetMethod(nameof(ISubscriptionTransport.SubscribeAsync)));
    }

    [Fact]
    public void Link_accepts_a_send_only_transport_for_send_only_clients()
    {
        var configuration = new LinkBuilder()
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("send-only", new SendOnlyTransport())
            .AddClient<ISendOnlyContract>(options => options.UseTransport("send-only"))
            .Build();

        Assert.Single(configuration.RegisteredTransports);
        Assert.IsAssignableFrom<ISendTransport>(
            configuration.RegisteredTransports.Single().Value);
    }

    [Fact]
    public void Link_rejects_publish_subscribe_capability_without_subscription_contract()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("metadata-only", new MetadataOnlyTransport())
                .AddServer<ITestContract, TestService>(server => server.UseTransport("metadata-only"))
                .Build());

        Assert.Contains(nameof(ISubscriptionTransport), exception.Message, StringComparison.Ordinal);
    }

    public interface ISendOnlyContract
    {
        ValueTask SendAsync(SendOnlyMessage message);
    }

    public interface ITestContract
    {
        ValueTask<TestResponse> RequestAsync(TestRequest request);
    }

    public sealed record SendOnlyMessage(string Value);

    public sealed record TestRequest(string Value);

    public sealed record TestResponse(string Value);

    private sealed class TestService : ITestContract
    {
        public ValueTask<TestResponse> RequestAsync(TestRequest request)
            => ValueTask.FromResult(new TestResponse(request.Value));
    }

    private sealed class SendOnlyTransport : ISendTransport
    {
        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MetadataOnlyTransport : IMessagingProtocol
    {
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
