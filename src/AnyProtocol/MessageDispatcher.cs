using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the message dispatcher implementation used by AnyProtocol applications.
/// </summary>
public sealed class MessageDispatcher
{
    private const string RegistrationKey = "anyprotocol.server-registration";
    private const string MethodKey = "anyprotocol.contract-method";
    private const string TransportKey = "anyprotocol.transport";
    private const string EventRegistrationKey = "anyprotocol.event-registration";
    private const string StreamResultKey = "anyprotocol.stream-result";
    private readonly IDependencyResolverFactory _resolverFactory;
    private readonly IMessageSerializer _serializer;
    private readonly MessageFilterDelegate _pipeline;
    private readonly IReadOnlyList<IErrorHandler> _errorHandlers;
    private readonly ConcurrentDictionary<(Type, MethodInfo), GeneratedServerHandler> _handlers = new();
    private readonly ConcurrentDictionary<(Type, MethodInfo), GeneratedServerStreamHandler>
        _streamHandlers = new();
    private readonly ConcurrentDictionary<(Type, Type), EventHandlerDelegate> _eventHandlers = new();

    /// <summary>
    /// Initializes a new instance of the MessageDispatcher class.
    /// </summary>
    /// <param name="resolverFactory">The resolver factory.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="filters">The filters.</param>
    /// <param name="errorHandlers">The error handlers.</param>
    public MessageDispatcher(
        IDependencyResolverFactory resolverFactory,
        IMessageSerializer serializer,
        IEnumerable<IMessageFilter>? filters = null,
        IEnumerable<IErrorHandler>? errorHandlers = null)
    {
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            InvokeHandlerAsync);
        _errorHandlers = (errorHandlers ?? []).ToArray();
    }

    /// <summary>
    /// Performs the dispatch async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="method">The method.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DispatchAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken = default,
        ProtocolKey? protocol = null)
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
                Services = scope.Resolver
            };
            context.Items[RegistrationKey] = registration;
            context.Items[MethodKey] = method;
            context.Items[TransportKey] = transport;
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
                transport is IDeadLetterTransport deadLetterTransport)
            {
                await NotifyErrorHandlersAsync(envelope, method, exception, cancellationToken)
                    .ConfigureAwait(false);
                await deadLetterTransport.SendToDeadLetterAsync(
                        method.Channel,
                        envelope,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                AnyProtocolDiagnostics.RecordDeadLetter(
                    envelope.Headers,
                    method.ContractName,
                    method.MethodName,
                    protocolName);
                return;
            }

            await SendFaultAsync(envelope, method, transport, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs the dispatch stream async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="method">The method.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    public async IAsyncEnumerable<TransportEnvelope> DispatchStreamAsync(
            ServerRegistration registration,
            ContractMethodDescriptor method,
            TransportEnvelope envelope,
            IMessagingProtocol transport,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            ProtocolKey? protocol = null)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(envelope);
            ArgumentNullException.ThrowIfNull(transport);
            if (method.Operation != ContractOperation.Stream)
            {
                throw new ArgumentException(
                    $"Method '{method.ContractName}.{method.MethodName}' is not a streaming operation.",
                    nameof(method));
            }

            var protocolName = protocol?.Value ?? registration.TransportName;
            using var deadlineSource = CreateDeadlineCancellation(envelope, cancellationToken);
            var dispatchCancellationToken = deadlineSource?.Token ?? cancellationToken;
            await using var scope = _resolverFactory.CreateAsyncScope();
            object request = method.RequestType == typeof(EmptyRequest)
                ? EmptyRequest.Instance
                : await _serializer.DeserializeAsync(
                        method.RequestType,
                        envelope.Body,
                        dispatchCancellationToken)
                      .ConfigureAwait(false) ??
                  throw new InvalidOperationException(
                      $"Request body for '{method.ContractName}.{method.MethodName}' was null.");
            var context = new MessageContext(
                envelope.Headers,
                envelope.Body,
                method.Channel,
                MessageType.Request,
                MessageDirection.Inbound,
                dispatchCancellationToken)
            {
                Message = request,
                Method = method,
                Services = scope.Resolver
            };
            context.Items[RegistrationKey] = registration;
            context.Items[MethodKey] = method;
            context.Items[TransportKey] = transport;
            context.Items[DiagnosticContext.TransportNameKey] = protocolName;

            var measurement = AnyProtocolDiagnostics.StartOperation(
                context,
                protocolName,
                "stream");
            using var activity = TracingFilter.StartActivity(context);
            var outcome = "cancelled";
            try
            {
            Exception? startError = null;
            try
            {
                await _pipeline(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dispatchCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                startError = exception;
            }

            if (startError is not null)
            {
                outcome = AnyProtocolDiagnostics.GetOutcome(
                    startError,
                    dispatchCancellationToken);
                TracingFilter.SetError(activity, startError, dispatchCancellationToken);
                yield return await CreateStreamFaultAsync(
                        envelope,
                        method,
                        startError,
                        dispatchCancellationToken)
                    .ConfigureAwait(false);
                yield break;
            }

            var stream = context.Items.TryGetValue(StreamResultKey, out var streamValue)
                ? streamValue as IAsyncEnumerable<object?>
                : null;
            if (stream is null)
            {
                var missingStream = new InvalidOperationException(
                    $"Streaming handler '{method.ContractName}.{method.MethodName}' returned no stream.");
                outcome = "fault";
                TracingFilter.SetError(activity, missingStream, dispatchCancellationToken);
                yield return await CreateStreamFaultAsync(
                        envelope,
                        method,
                        missingStream,
                        dispatchCancellationToken)
                    .ConfigureAwait(false);
                yield break;
            }

            await using var enumerator = stream.GetAsyncEnumerator(dispatchCancellationToken);
            long sequence = 0;
            while (true)
            {
                bool hasNext;
                object? item = null;
                Exception? iterationError = null;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        item = enumerator.Current;
                    }
                }
                catch (OperationCanceledException)
                    when (dispatchCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    hasNext = false;
                    iterationError = exception;
                }

                if (iterationError is not null)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(
                        iterationError,
                        dispatchCancellationToken);
                    TracingFilter.SetError(activity, iterationError, dispatchCancellationToken);
                    yield return await CreateStreamFaultAsync(
                            envelope,
                            method,
                            iterationError,
                            dispatchCancellationToken)
                        .ConfigureAwait(false);
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                ReadOnlyMemory<byte> body = default;
                Exception? serializationError = null;
                try
                {
                    body = item is null
                        ? await _serializer.SerializeAsync<object?>(
                                null,
                                dispatchCancellationToken)
                            .ConfigureAwait(false)
                        : await _serializer.SerializeAsync(
                                item.GetType(),
                                item,
                                dispatchCancellationToken)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    serializationError = exception;
                }

                if (serializationError is not null)
                {
                    outcome = "fault";
                    TracingFilter.SetError(
                        activity,
                        serializationError,
                        dispatchCancellationToken);
                    yield return await CreateStreamFaultAsync(
                            envelope,
                            method,
                            serializationError,
                            dispatchCancellationToken)
                        .ConfigureAwait(false);
                    yield break;
                }

                var headers = CreateResponseHeaders(envelope, MessageType.StreamItem);
                headers[HeaderNames.StreamSequence] = sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                yield return new TransportEnvelope(headers, body);
                sequence++;
            }

            outcome = "success";
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            yield return new TransportEnvelope(
                CreateResponseHeaders(envelope, MessageType.StreamComplete),
                ReadOnlyMemory<byte>.Empty);
            }
            finally
            {
                measurement.Complete(outcome);
            }
    }

    /// <summary>
    /// Performs the dispatch routing fault async operation.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DispatchRoutingFaultAsync(
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        string message,
        CancellationToken cancellationToken = default)
        => SendFaultAsync(
            envelope,
            method: null,
            transport,
            new AnyProtocolFaultException(new FaultMessage("route_not_found", message)),
            cancellationToken);

    /// <summary>
    /// Performs the dispatch event async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DispatchEventAsync(
        EventRegistration registration,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _resolverFactory.CreateAsyncScope();
            var message = await _serializer.DeserializeAsync(
                    registration.EventType,
                    envelope.Body,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    $"Event body for '{registration.EventType.FullName}' was null.");
            var context = new MessageContext(
                envelope.Headers,
                envelope.Body,
                registration.Channel,
                MessageType.Event,
                MessageDirection.Inbound,
                cancellationToken)
            {
                Message = message,
                Services = scope.Resolver
            };
            context.Items[EventRegistrationKey] = registration;
            context.Items[TransportKey] = transport;
            context.Items[DiagnosticContext.TransportNameKey] = registration.TransportName;
            await _pipeline(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (transport is IDeadLetterTransport deadLetterTransport)
            {
                await NotifyErrorHandlersAsync(envelope, null, exception, cancellationToken)
                    .ConfigureAwait(false);
                await deadLetterTransport.SendToDeadLetterAsync(
                        registration.Channel,
                        envelope,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                AnyProtocolDiagnostics.RecordDeadLetter(
                    envelope.Headers,
                    registration.EventType.FullName ?? registration.EventType.Name,
                    null,
                    registration.TransportName);
                return;
            }

            await SendFaultAsync(envelope, null, transport, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask InvokeHandlerAsync(IMessageContext context)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (context.Items.TryGetValue(EventRegistrationKey, out var eventValue))
            {
                var eventRegistration = (EventRegistration)eventValue!;
                var eventHandler = context.Services!.Resolve(eventRegistration.HandlerType) ??
                                   throw new InvalidOperationException(
                                       $"Event handler '{eventRegistration.HandlerType.FullName}' is not registered.");
                if (!RuntimeFeature.IsDynamicCodeSupported)
                {
                    throw new InvalidOperationException(
                        $"Native AOT event dispatch for '{eventRegistration.EventType.FullName}' " +
                        $"to '{eventRegistration.HandlerType.FullName}' has no generated delegate. " +
                        "Event handler generation is required when dynamic code is disabled.");
                }

                var eventInvoker = _eventHandlers.GetOrAdd(
                    (eventRegistration.HandlerType, eventRegistration.EventType),
                    static key => CompileEventHandler(key.Item2));
                await eventInvoker(eventHandler, context.Message!).ConfigureAwait(false);
                AnyProtocolDiagnostics.RecordHandler(
                    context,
                    eventRegistration.TransportName,
                    startedAt,
                    "success");
                return;
            }

            var registration = (ServerRegistration)context.Items[RegistrationKey]!;
            var method = (ContractMethodDescriptor)context.Items[MethodKey]!;
            var transport = (IMessagingProtocol)context.Items[TransportKey]!;
            var implementation = context.Services!.Resolve(registration.ImplementationType) ??
                                 throw new InvalidOperationException(
                                     $"Service '{registration.ImplementationType.FullName}' is not registered.");
            if (method.Operation == ContractOperation.Stream)
            {
                if (!GeneratedServerDispatchRegistry.TryGetStreamHandler(
                        registration.ContractType,
                        method.MethodName,
                        out var streamHandler))
                {
                    if (!RuntimeFeature.IsDynamicCodeSupported)
                    {
                        throw MissingGeneratedDispatch(registration.ContractType, method);
                    }

                    streamHandler = _streamHandlers.GetOrAdd(
                        (registration.ImplementationType, method.Method),
                        static key => CompileStreamHandler(key.Item2));
                }

                context.Items[StreamResultKey] = streamHandler(
                    implementation,
                    context.Message,
                    context.CancellationToken);
                AnyProtocolDiagnostics.RecordHandler(
                    context,
                    DiagnosticContext.GetTransportName(context),
                    startedAt,
                    "success");
                return;
            }

            if (!GeneratedServerDispatchRegistry.TryGetHandler(
                    registration.ContractType,
                    method.MethodName,
                    out var handler))
            {
                if (!RuntimeFeature.IsDynamicCodeSupported)
                {
                    throw MissingGeneratedDispatch(registration.ContractType, method);
                }

                handler = _handlers.GetOrAdd(
                    (registration.ImplementationType, method.Method),
                    static key => CompileHandler(key.Item2));
            }

            var result = await handler(implementation, context.Message, context.CancellationToken)
                .ConfigureAwait(false);

            if (method.Operation == ContractOperation.Request || method.ExpectReply)
            {
                await SendResponseAsync(
                        new TransportEnvelope(context.Headers, context.Body),
                        method,
                        transport,
                        method.ExpectReply && method.Operation == ContractOperation.Send
                            ? Unit.Value
                            : result,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            AnyProtocolDiagnostics.RecordHandler(
                context,
                DiagnosticContext.GetTransportName(context),
                startedAt,
                "success");
        }
        catch (Exception exception)
        {
            AnyProtocolDiagnostics.RecordHandler(
                context,
                DiagnosticContext.GetTransportName(context),
                startedAt,
                AnyProtocolDiagnostics.GetOutcome(exception, context.CancellationToken));
            throw;
        }
    }

    private async ValueTask SendResponseAsync(
        TransportEnvelope request,
        ContractMethodDescriptor method,
        IMessagingProtocol transport,
        object? response,
        CancellationToken cancellationToken)
    {
        var replyTo = request.Headers[HeaderNames.ReplyTo] ??
                      throw new InvalidOperationException(
                          $"Request '{method.ContractName}.{method.MethodName}' has no reply channel.");
        var headers = CreateResponseHeaders(request, MessageType.Response);
        var body = response is null
            ? await _serializer.SerializeAsync<object?>(null, cancellationToken).ConfigureAwait(false)
            : await _serializer.SerializeAsync(response.GetType(), response, cancellationToken)
                .ConfigureAwait(false);
        var sendTransport = transport as ISendTransport ??
            throw new InvalidOperationException(
                "Sending a response requires an ISendTransport implementation.");
        await sendTransport.SendAsync(replyTo, new TransportEnvelope(headers, body), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask SendFaultAsync(
        TransportEnvelope request,
        ContractMethodDescriptor? method,
        IMessagingProtocol transport,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var replyTo = request.Headers[HeaderNames.ReplyTo];
        await NotifyErrorHandlersAsync(request, method, exception, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(replyTo))
        {
            if (_errorHandlers.Count == 0)
            {
                System.Diagnostics.Trace.TraceError(
                    "Unhandled AnyProtocol event error for '{0}.{1}'; exception type '{2}'.",
                    method?.ContractName ?? request.Headers[HeaderNames.Contract] ?? "unknown",
                    method?.MethodName ?? request.Headers[HeaderNames.Method] ?? "unknown",
                    exception.GetType().FullName);
            }

            return;
        }

        var fault = exception is AnyProtocolFaultException knownFault
            ? knownFault.Fault
            : new FaultMessage(
                "handler_failed",
                exception.Message,
                exception.GetType().FullName,
                Retryable: false);
        var body = await _serializer.SerializeAsync(fault, cancellationToken).ConfigureAwait(false);
        try
        {
            var sendTransport = transport as ISendTransport ??
                throw new InvalidOperationException(
                    "Sending a fault requires an ISendTransport implementation.");
            await sendTransport.SendAsync(
                    replyTo,
                    new TransportEnvelope(CreateResponseHeaders(request, MessageType.Fault), body),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception deliveryException) when (deliveryException is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceError(
                "Failed to deliver a AnyProtocol fault response; exception type '{0}'.",
                deliveryException.GetType().FullName);
        }
    }

    private async ValueTask<TransportEnvelope> CreateStreamFaultAsync(
        TransportEnvelope request,
        ContractMethodDescriptor method,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await NotifyErrorHandlersAsync(request, method, exception, cancellationToken)
            .ConfigureAwait(false);
        var fault = exception is AnyProtocolFaultException knownFault
            ? knownFault.Fault
            : new FaultMessage(
                "handler_failed",
                exception.Message,
                exception.GetType().FullName,
                Retryable: false);
        var body = await _serializer.SerializeAsync(fault, cancellationToken).ConfigureAwait(false);
        return new TransportEnvelope(CreateResponseHeaders(request, MessageType.Fault), body);
    }

    private async ValueTask NotifyErrorHandlersAsync(
        TransportEnvelope request,
        ContractMethodDescriptor? method,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_errorHandlers.Count == 0)
        {
            return;
        }

        var context = new MessageContext(
            request.Headers,
            request.Body,
            method?.Channel ?? request.Headers[HeaderNames.Channel] ?? string.Empty,
            request.Headers.Get(HeaderNames.MessageType, MessageType.Event),
            MessageDirection.Inbound,
            cancellationToken)
        {
            Method = method,
            Exception = exception
        };
        foreach (var errorHandler in _errorHandlers)
        {
            try
            {
                await errorHandler.HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception handlerException)
            {
                System.Diagnostics.Trace.TraceError(
                    "AnyProtocol error handler '{0}' failed while reporting '{1}'; " +
                    "exception type '{2}'.",
                    errorHandler.GetType().FullName,
                    exception.GetType().FullName,
                    handlerException.GetType().FullName);
            }
        }
    }

    private static CancellationTokenSource? CreateDeadlineCancellation(
        TransportEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(
                request.Headers[HeaderNames.Deadline],
                out var deadline))
        {
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - DateTimeOffset.UtcNow;
        source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return source;
    }

    private static MessageHeaders CreateResponseHeaders(
        TransportEnvelope request,
        MessageType messageType)
        => new()
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.CorrelationId] =
                request.Headers[HeaderNames.CorrelationId] ??
                request.Headers[HeaderNames.MessageId],
            [HeaderNames.MessageType] = messageType.ToString(),
            [HeaderNames.ContentType] =
                request.Headers[HeaderNames.ContentType] ?? "application/x-anyprotocol"
        };

    private static InvalidOperationException MissingGeneratedDispatch(
        Type contractType,
        ContractMethodDescriptor method)
        => new(
            $"Native AOT server dispatch for '{contractType.FullName}.{method.MethodName}' has no " +
            "generated delegate. Reference AnyProtocol.Generator and register the closed contract " +
            "directly with LinkBuilder.AddServer<TContract, TImplementation>().");

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "JIT server dispatch compiles expression trees.")]
    private static GeneratedServerHandler CompileHandler(MethodInfo method)
    {
        if (method.ReturnType.IsGenericType &&
            method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            throw new NotSupportedException(
                $"Server streaming dispatch for '{method.DeclaringType?.FullName}.{method.Name}' " +
                "requires a native streaming host.");
        }

        var target = Expression.Parameter(typeof(object), "target");
        var request = Expression.Parameter(typeof(object), "request");
        var cancellationToken = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var arguments = method.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                ? (Expression)cancellationToken
                : Expression.Convert(request, parameter.ParameterType))
            .ToArray();
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            arguments);
        var wrapper = GetAwaitWrapper(method.ReturnType);
        var body = Expression.Call(wrapper, call);
        return Expression.Lambda<GeneratedServerHandler>(
                body,
                target,
                request,
                cancellationToken)
            .Compile();
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "JIT event dispatch constructs and compiles a closed event handler.")]
    private static EventHandlerDelegate CompileEventHandler(Type eventType)
    {
        var consumerType = typeof(IEventConsumer<>).MakeGenericType(eventType);
        var consumeMethod = consumerType.GetMethod(nameof(IEventConsumer<object>.ConsumeAsync))!;
        var target = Expression.Parameter(typeof(object), "target");
        var message = Expression.Parameter(typeof(object), "message");
        var body = Expression.Call(
            Expression.Convert(target, consumerType),
            consumeMethod,
            Expression.Convert(message, eventType));
        return Expression.Lambda<EventHandlerDelegate>(body, target, message).Compile();
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "JIT streaming dispatch constructs and compiles a closed stream handler.")]
    private static GeneratedServerStreamHandler CompileStreamHandler(MethodInfo method)
    {
        var itemType = method.ReturnType.GetGenericArguments()[0];
        var target = Expression.Parameter(typeof(object), "target");
        var request = Expression.Parameter(typeof(object), "request");
        var cancellationToken = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var arguments = method.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                ? (Expression)cancellationToken
                : Expression.Convert(request, parameter.ParameterType))
            .ToArray();
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            arguments);
        var castMethod = typeof(MessageDispatcher)
            .GetMethod(nameof(CastStreamAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(itemType);
        var body = Expression.Call(castMethod, call, cancellationToken);
        return Expression.Lambda<GeneratedServerStreamHandler>(
                body,
                target,
                request,
                cancellationToken)
            .Compile();
    }

    private static async IAsyncEnumerable<object?> CastStreamAsync<T>(
        IAsyncEnumerable<T> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static MethodInfo GetAwaitWrapper(Type returnType)
    {
        if (returnType == typeof(Task))
        {
            return typeof(MessageDispatcher)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(
                    method =>
                        method.Name == nameof(AwaitTaskAsync) &&
                        !method.IsGenericMethod);
        }

        if (returnType == typeof(ValueTask))
        {
            return typeof(MessageDispatcher)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(
                    method =>
                        method.Name == nameof(AwaitValueTaskAsync) &&
                        !method.IsGenericMethod);
        }

        var definition = returnType.GetGenericTypeDefinition();
        var methodName = definition == typeof(Task<>)
            ? nameof(AwaitTaskAsync)
            : nameof(AwaitValueTaskAsync);
        return typeof(MessageDispatcher).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == methodName && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(returnType.GetGenericArguments()[0]);
    }

    private static async ValueTask<object?> AwaitTaskAsync(Task task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    private static async ValueTask<object?> AwaitTaskAsync<T>(Task<T> task)
        => await task.ConfigureAwait(false);

    private static async ValueTask<object?> AwaitValueTaskAsync(ValueTask task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    private static async ValueTask<object?> AwaitValueTaskAsync<T>(ValueTask<T> task)
        => await task.ConfigureAwait(false);

    private delegate ValueTask EventHandlerDelegate(object target, object message);
}
