using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Serializer.Abstraction.Exceptions;
using AnyProtocol.Serializer.MessagePack;
using AnyProtocol.Serializer.TextJson;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class MessageSerializerConformanceTests
{
    public static IEnumerable<object[]> Serializers()
    {
        yield return [new TextJsonMessageSerializer(), new byte[] { (byte)'{' }];
        yield return [new MessagePackMessageSerializer(), new byte[] { 0xc1 }];
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Runtime_type_async_deserialization_wraps_invalid_payloads(
        IMessageSerializer serializer,
        byte[] payload)
    {
        using (serializer)
        {
            var exception = await Assert.ThrowsAsync<SerializationFailedException>(
                () => serializer.DeserializeAsync(typeof(TestMessage), payload).AsTask());

            Assert.NotNull(exception.InnerException);
            Assert.Contains(typeof(TestMessage).FullName!, exception.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Runtime_type_async_deserialization_preserves_caller_cancellation(
        IMessageSerializer serializer,
        byte[] payload)
    {
        using (serializer)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => serializer.DeserializeAsync(typeof(TestMessage), payload, cancellation.Token).AsTask());
        }
    }

    private sealed record TestMessage(string Value);
}
