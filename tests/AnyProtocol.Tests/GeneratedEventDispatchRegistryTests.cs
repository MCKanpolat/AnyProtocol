using Xunit;

namespace AnyProtocol.Tests;

public sealed class GeneratedEventDispatchRegistryTests
{
    [Fact]
    public async Task Registry_invokes_registered_handler_and_accepts_the_same_delegate()
    {
        var handler = new TestHandler();

        GeneratedEventDispatchRegistry.Register(
            typeof(TestEvent),
            typeof(TestHandler),
            Dispatch);
        GeneratedEventDispatchRegistry.Register(
            typeof(TestEvent),
            typeof(TestHandler),
            Dispatch);

        Assert.True(
            GeneratedEventDispatchRegistry.TryGetHandler(
                typeof(TestEvent),
                typeof(TestHandler),
                out var generated));

        await generated(handler, new TestEvent("registered"));

        Assert.Equal("registered", handler.Value);
    }

    [Fact]
    public void Registry_rejects_a_conflicting_delegate()
    {
        GeneratedEventDispatchRegistry.Register(
            typeof(ConflictEvent),
            typeof(ConflictHandler),
            ConflictDispatch);

        var exception = Assert.Throws<InvalidOperationException>(
            () => GeneratedEventDispatchRegistry.Register(
                typeof(ConflictEvent),
                typeof(ConflictHandler),
                AlternativeDispatch));

        Assert.Contains(typeof(ConflictEvent).FullName!, exception.Message);
        Assert.Contains(typeof(ConflictHandler).FullName!, exception.Message);
    }

    private static ValueTask Dispatch(object target, object message)
    {
        ((TestHandler)target).Value = ((TestEvent)message).Value;
        return ValueTask.CompletedTask;
    }

    private static ValueTask AlternativeDispatch(object target, object message)
        => ValueTask.CompletedTask;

    private sealed record TestEvent(string Value);

    private sealed class TestHandler
    {
        public string? Value { get; set; }
    }

    private sealed record ConflictEvent;

    private sealed class ConflictHandler;

    private static ValueTask ConflictDispatch(object target, object message)
        => ValueTask.CompletedTask;
}
