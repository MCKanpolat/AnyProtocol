using AnyProtocol.Serializer.Abstraction.Exceptions;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class SerializationFailedExceptionTests
{
    [Fact]
    public void All_constructors_preserve_message_and_inner_exception()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new SerializationFailedException());
        Assert.Equal("message", new SerializationFailedException("message").Message);

        var exception = new SerializationFailedException("wrapped", inner);
        Assert.Equal("wrapped", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }
}
