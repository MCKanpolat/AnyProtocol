using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol;

internal sealed class MessageInvocation(
    CancellationToken cancellationToken,
    IDependencyResolver? services = null,
    DispatchState? dispatchState = null,
    InvocationResult? result = null)
{
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public IDependencyResolver? Services { get; } = services;

    public DispatchState? DispatchState { get; } = dispatchState;

    public InvocationResult Result { get; } = result ?? new InvocationResult();

    public MessageInvocation WithCancellation(CancellationToken cancellationToken)
        => new(cancellationToken, Services, DispatchState, Result);

    public MessageInvocation WithServices(IDependencyResolver services)
        => new(CancellationToken, services, DispatchState, Result);
}

internal sealed class InvocationResult
{
    public TransportEnvelope? Response { get; set; }

    public object? HandlerResult { get; set; }

    public IAsyncEnumerable<object?>? StreamResult { get; set; }

    public void ResetResponse() => Response = null;
}

internal interface IRuntimeMessageContext
{
    MessageInvocation Invocation { get; }
}

internal sealed class CancellationMessageContext : IMessageContext, IRuntimeMessageContext
{
    private readonly IMessageContext _inner;

    public CancellationMessageContext(IMessageContext inner, MessageInvocation invocation)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
    }

    public IMessageHeaders Headers => _inner.Headers;

    public ReadOnlyMemory<byte> Body
    {
        get => _inner.Body;
        set => _inner.Body = value;
    }

    public object? Message
    {
        get => _inner.Message;
        set => _inner.Message = value;
    }

    public string Channel
    {
        get => _inner.Channel;
        set => _inner.Channel = value;
    }

    public MessageType MessageType
    {
        get => _inner.MessageType;
        set => _inner.MessageType = value;
    }

    public MessageDirection Direction
    {
        get => _inner.Direction;
        set => _inner.Direction = value;
    }

    public ContractMethodDescriptor? Method
    {
        get => _inner.Method;
        set => _inner.Method = value;
    }

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken CancellationToken => Invocation.CancellationToken;

    public MessageInvocation Invocation { get; }
}

internal static class MessageContextRuntime
{
    public static MessageInvocation Get(IMessageContext context)
        => context is IRuntimeMessageContext runtime
            ? runtime.Invocation
            : throw new InvalidOperationException("AnyProtocol requires its runtime message context.");

    public static IMessageContext WithCancellation(
        IMessageContext context,
        CancellationToken cancellationToken)
        => new CancellationMessageContext(context, Get(context).WithCancellation(cancellationToken));

    public static IMessageContext WithServices(IMessageContext context, IDependencyResolver services)
        => new CancellationMessageContext(context, Get(context).WithServices(services));
}
