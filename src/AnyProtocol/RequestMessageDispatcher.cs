using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Dispatches request and send operations to server handlers.
/// </summary>
internal sealed class RequestMessageDispatcher
{
    private readonly IDependencyResolverFactory _resolverFactory;
    private readonly IMessageSerializer _serializer;
    private readonly HandlerInvoker _handlerInvoker;
    private readonly ReplyMessageSender _replyMessageSender;
    private readonly FaultMessageSender _faultMessageSender;
    private readonly MessageFilterDelegate _pipeline;
    private readonly MessageFilterDelegate _invokePipeline;

    public RequestMessageDispatcher(
        IDependencyResolverFactory resolverFactory,
        IMessageSerializer serializer,
        IEnumerable<IMessageFilter> filters,
        HandlerInvoker handlerInvoker,
        ReplyMessageSender replyMessageSender,
        FaultMessageSender faultMessageSender)
    {
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _handlerInvoker = handlerInvoker ?? throw new ArgumentNullException(nameof(handlerInvoker));
        _replyMessageSender = replyMessageSender ??
                              throw new ArgumentNullException(nameof(replyMessageSender));
        _faultMessageSender = faultMessageSender ??
                              throw new ArgumentNullException(nameof(faultMessageSender));
        _pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            InvokeAndReplyAsync);
        _invokePipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            InvokeHandlerAsync);
    }

    public async ValueTask DispatchAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken,
        ProtocolKey? protocol)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(transport);

        var protocolName = protocol?.Value ?? registration.TransportName;
        try
        {
            await using var scope = _resolverFactory.CreateAsyncScope();
            object request = method.RequestType == typeof(EmptyRequest)
                ? EmptyRequest.Instance
                : await _serializer.DeserializeAsync(method.RequestType, envelope.Body, cancellationToken)
                      .ConfigureAwait(false) ??
                  throw new InvalidOperationException(
                      $"Request body for '{method.ContractName}.{method.MethodName}' was null.");
            var context = new MessageContext(
                envelope.Headers,
                envelope.Body,
                method.Channel,
                envelope.Headers.Get(HeaderNames.MessageType, MessageType.Request),
                MessageDirection.Inbound,
                cancellationToken)
            {
                Message = request,
                Method = method,
                Invocation = new MessageInvocation(
                    cancellationToken,
                    scope.Resolver,
                    new DispatchState(registration, null, transport))
            };
            context.Items[DiagnosticContext.TransportNameKey] = protocolName;
            await _pipeline(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (envelope.Headers.Get(HeaderNames.MessageType, MessageType.Request) == MessageType.Event &&
                await _faultMessageSender.TrySendToDeadLetterAsync(
                        envelope,
                        method,
                        transport,
                        method.Channel,
                        method.ContractName,
                        method.MethodName,
                        protocolName,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            await _faultMessageSender.SendAsync(envelope, method, transport, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<DispatchInvocationResult> InvokeAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        CancellationToken cancellationToken,
        ProtocolKey? protocol)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(envelope);

        var protocolName = protocol?.Value ?? registration.TransportName;
        try
        {
            await using var scope = _resolverFactory.CreateAsyncScope();
            object request = method.RequestType == typeof(EmptyRequest)
                ? EmptyRequest.Instance
                : await _serializer.DeserializeAsync(method.RequestType, envelope.Body, cancellationToken)
                      .ConfigureAwait(false) ??
                  throw new InvalidOperationException(
                      $"Request body for '{method.ContractName}.{method.MethodName}' was null.");
            var context = new MessageContext(
                envelope.Headers,
                envelope.Body,
                method.Channel,
                envelope.Headers.Get(HeaderNames.MessageType, MessageType.Request),
                MessageDirection.Inbound,
                cancellationToken)
            {
                Message = request,
                Method = method,
                Invocation = new MessageInvocation(
                    cancellationToken,
                    scope.Resolver,
                    new DispatchState(registration, null, null))
            };
            context.Items[DiagnosticContext.TransportNameKey] = protocolName;
            await _invokePipeline(context).ConfigureAwait(false);
            return new DispatchInvocationResult(
                MessageContextRuntime.Get(context).Result.HandlerResult,
                Fault: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _faultMessageSender.NotifyErrorHandlersAsync(
                    envelope,
                    method,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DispatchInvocationResult(
                Result: null,
                FaultMessageSender.CreateFault(exception));
        }
    }

    private async ValueTask InvokeAndReplyAsync(IMessageContext context)
    {
        await _handlerInvoker.InvokeAsync(context).ConfigureAwait(false);

        var method = context.Method ??
                     throw new InvalidOperationException("Contract method was not initialized.");
        if (method.Operation != ContractOperation.Request && !method.ExpectReply)
        {
            return;
        }

        var invocation = MessageContextRuntime.Get(context);
        var state = invocation.DispatchState ??
                    throw new InvalidOperationException("Dispatch state was not initialized.");
        await _replyMessageSender.SendAsync(
                new TransportEnvelope(context.Headers, context.Body),
                method,
                state.Transport ?? throw new InvalidOperationException(
                    "A transport is required when dispatching a wire reply."),
                method.ExpectReply && method.Operation == ContractOperation.Send
                    ? Unit.Value
                : invocation.Result.HandlerResult,
                context.CancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask InvokeHandlerAsync(IMessageContext context)
        => _handlerInvoker.InvokeAsync(context);
}

internal sealed record DispatchInvocationResult(object? Result, FaultMessage? Fault);
