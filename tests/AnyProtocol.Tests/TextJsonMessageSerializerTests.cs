using System.Text;
using System.Text.Json;
using AnyProtocol.Serializer.Abstraction.Exceptions;
using AnyProtocol.Serializer.TextJson;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class TextJsonMessageSerializerTests
{
    [Fact]
    public async Task Runtime_type_deserialize_async_wraps_invalid_json_like_generic_overload()
    {
        var serializer = new TextJsonMessageSerializer();
        var bytes = Encoding.UTF8.GetBytes("{ invalid-json }");

        var genericException = await Assert.ThrowsAsync<SerializationFailedException>(
            () => serializer.DeserializeAsync<TestMessage>(bytes).AsTask());
        var runtimeException = await Assert.ThrowsAsync<SerializationFailedException>(
            () => serializer.DeserializeAsync(typeof(TestMessage), bytes).AsTask());

        Assert.Equal(genericException.Message, runtimeException.Message);
        Assert.IsType<JsonException>(genericException.InnerException);
        Assert.IsType<JsonException>(runtimeException.InnerException);
    }

    [Fact]
    public async Task Runtime_type_deserialize_async_preserves_caller_cancellation()
    {
        var serializer = new TextJsonMessageSerializer();
        var bytes = Encoding.UTF8.GetBytes("""{"value":"ok"}""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => serializer.DeserializeAsync(typeof(TestMessage), bytes, cancellation.Token).AsTask());
    }

    private sealed record TestMessage(string Value);
}
