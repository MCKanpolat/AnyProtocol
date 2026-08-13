using AnyProtocol.Tests.Shared;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class BrokerTestModeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void RequireBrokerTests_parses_explicit_mode(string? value, bool expected)
        => Assert.Equal(expected, BrokerTestMode.RequireBrokerTests(value));

    [Fact]
    public void RequireBrokerTests_rejects_unknown_mode()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BrokerTestMode.RequireBrokerTests("required"));

        Assert.Contains(BrokerTestMode.RequireBrokerTestsEnvironmentVariable, exception.Message);
    }
}
