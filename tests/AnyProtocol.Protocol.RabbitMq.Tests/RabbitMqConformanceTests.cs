using AnyProtocol.Conformance;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqConformanceTests(RabbitMqFixture fixture)
    : TransportConformanceTests, IClassFixture<RabbitMqFixture>
{
    protected override TimeSpan Timeout => TimeSpan.FromSeconds(20);

    protected override IMessagingProtocol CreateTransport() => fixture.CreateTransport();
}
