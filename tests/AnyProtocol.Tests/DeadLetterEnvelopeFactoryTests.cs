using AnyProtocol;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class DeadLetterEnvelopeFactoryTests
{
    [Fact]
    public void Default_policy_removes_credentials_and_non_replay_headers_without_mutating_source()
    {
        var source = new MessageHeaders
        {
            [HeaderNames.MessageId] = "message-1",
            [HeaderNames.Contract] = "Orders.Contracts.IOrders",
            ["AUTHORIZATION"] = "Bearer canary",
            ["X-Api-Key"] = "canary-key",
            ["application-note"] = "private"
        };
        var envelope = new TransportEnvelope(source, new byte[] { 1, 2, 3 });

        var deadLetter = new DeadLetterEnvelopeFactory().Create(
            "orders",
            envelope,
            new InvalidOperationException("canary exception"));

        Assert.Equal("message-1", deadLetter.Headers[HeaderNames.MessageId]);
        Assert.Equal("Orders.Contracts.IOrders", deadLetter.Headers[HeaderNames.Contract]);
        Assert.False(deadLetter.Headers.ContainsKey("AUTHORIZATION"));
        Assert.False(deadLetter.Headers.ContainsKey("X-Api-Key"));
        Assert.False(deadLetter.Headers.ContainsKey("application-note"));
        Assert.Equal("handler_failed", deadLetter.Headers[HeaderNames.DeadLetterError]);
        Assert.Equal(nameof(InvalidOperationException), deadLetter.Headers[HeaderNames.DeadLetterErrorType]);
        Assert.Empty(deadLetter.Body.ToArray());
        Assert.Equal("Bearer canary", source["authorization"]);
        Assert.Equal("private", source["application-note"]);
    }

    [Fact]
    public void Fingerprint_policy_requires_key_and_does_not_retain_plaintext()
    {
        Assert.Throws<ArgumentException>(
            () => new DeadLetterEnvelopeFactory(bodyPolicy: DeadLetterBodyPolicy.FingerprintOnly));

        var deadLetter = new DeadLetterEnvelopeFactory(
                bodyPolicy: DeadLetterBodyPolicy.FingerprintOnly,
                fingerprintKey: "fingerprint-key"u8)
            .Create(
                "orders",
                new TransportEnvelope(new MessageHeaders(), new byte[] { 1, 2, 3 }),
                new InvalidOperationException());

        Assert.True(deadLetter.Headers.ContainsKey(HeaderNames.DeadLetterBodyFingerprint));
        Assert.Empty(deadLetter.Body.ToArray());
    }
}
