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

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Generic_async_deserialization_wraps_invalid_payloads(
        IMessageSerializer serializer,
        byte[] payload)
    {
        using (serializer)
        {
            var exception = await Assert.ThrowsAsync<SerializationFailedException>(
                () => serializer.DeserializeAsync<TestMessage>(payload).AsTask());

            Assert.NotNull(exception.InnerException);
            Assert.Contains(typeof(TestMessage).FullName!, exception.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Sync_serialization_wraps_failures(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new ThrowingMessage();

            var genericException = Assert.Throws<SerializationFailedException>(
                () => serializer.Serialize(message));
            var runtimeException = Assert.Throws<SerializationFailedException>(
                () => serializer.Serialize(typeof(ThrowingMessage), message));

            Assert.NotNull(genericException.InnerException);
            Assert.NotNull(runtimeException.InnerException);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Async_serialization_wraps_failures(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new ThrowingMessage();

            var genericException = await Assert.ThrowsAsync<SerializationFailedException>(
                () => serializer.SerializeAsync(message).AsTask());
            var runtimeException = await Assert.ThrowsAsync<SerializationFailedException>(
                () => serializer.SerializeAsync(typeof(ThrowingMessage), message).AsTask());

            Assert.NotNull(genericException.InnerException);
            Assert.NotNull(runtimeException.InnerException);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Async_serialization_preserves_caller_cancellation(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => serializer.SerializeAsync(
                    new TestMessage("cancelled"),
                    cancellation.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => serializer.SerializeAsync(
                    typeof(TestMessage),
                    new TestMessage("cancelled"),
                    cancellation.Token).AsTask());
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Generic_sync_round_trip_preserves_the_message(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new TestMessage("sync-generic");

            var payload = serializer.Serialize(message);
            var decoded = serializer.Deserialize<TestMessage>(payload);

            Assert.Equal(message, decoded);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Generic_async_round_trip_preserves_the_message(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new TestMessage("async-generic");

            var payload = await serializer.SerializeAsync(message);
            var decoded = await serializer.DeserializeAsync<TestMessage>(payload);

            Assert.Equal(message, decoded);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Runtime_sync_round_trip_preserves_the_message(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new TestMessage("sync-runtime");

            var payload = serializer.Serialize(typeof(TestMessage), message);
            var decoded = serializer.Deserialize(typeof(TestMessage), payload);

            Assert.Equal(message, decoded);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public async Task Runtime_async_round_trip_preserves_the_message(
        IMessageSerializer serializer,
        byte[] _)
    {
        using (serializer)
        {
            var message = new TestMessage("async-runtime");

            var payload = await serializer.SerializeAsync(typeof(TestMessage), message);
            var decoded = await serializer.DeserializeAsync(typeof(TestMessage), payload);

            Assert.Equal(message, decoded);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Sync_deserialization_wraps_invalid_payloads(
        IMessageSerializer serializer,
        byte[] payload)
    {
        using (serializer)
        {
            var genericException = Assert.Throws<SerializationFailedException>(
                () => serializer.Deserialize<TestMessage>(payload));
            var runtimeException = Assert.Throws<SerializationFailedException>(
                () => serializer.Deserialize(typeof(TestMessage), payload));

            Assert.NotNull(genericException.InnerException);
            Assert.NotNull(runtimeException.InnerException);
        }
    }

    public sealed record TestMessage(string Value);

    [MessagePack.MessagePackObject]
    public sealed class ThrowingMessage
    {
        [MessagePack.Key(0)]
        public string Value => throw new InvalidOperationException("serialization failure");
    }
}
