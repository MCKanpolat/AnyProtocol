using AnyProtocol.Abstraction;
using AnyProtocol.MessageIdGenerator.Ulid;
using Xunit;

namespace AnyProtocol.MessageIdGenerator.Ulid.Tests;

public sealed class UlidMessageIdGeneratorTests
{
    [Fact]
    public void Generate_returns_a_crockford_base32_ulid()
    {
        IMessageIdGenerator generator = new UlidMessageIdGenerator();

        var messageId = generator.Generate();

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", messageId);
    }

    [Fact]
    public void Generate_returns_unique_ids_for_repeated_calls()
    {
        var generator = new UlidMessageIdGenerator();

        var messageIds = Enumerable.Range(0, 256)
            .Select(_ => generator.Generate())
            .ToArray();

        Assert.Equal(messageIds.Length, messageIds.Distinct(StringComparer.Ordinal).Count());
    }
}
