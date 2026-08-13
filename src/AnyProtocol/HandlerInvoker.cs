using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;

namespace AnyProtocol;

/// <summary>
/// Resolves and invokes generated or JIT server and event handlers.
/// </summary>
internal sealed class HandlerInvoker
{
    private readonly ConcurrentDictionary<(Type, MethodInfo), GeneratedServerHandler> _handlers = new();
    private readonly ConcurrentDictionary<(Type, MethodInfo), GeneratedServerStreamHandler>
        _streamHandlers = new();
    private readonly ConcurrentDictionary<(Type, Type), GeneratedEventHandler> _eventHandlers = new();

    public async ValueTask InvokeAsync(IMessageContext context)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var invocation = MessageContextRuntime.Get(context);
        var dispatchState = invocation.DispatchState ??
                            throw new InvalidOperationException("Dispatch state was not initialized.");
        try
        {
            if (dispatchState.EventRegistration is not null)
            {
                var eventRegistration = dispatchState.EventRegistration;
                var eventHandler = invocation.Services!.Resolve(eventRegistration.HandlerType) ??
                                   throw new InvalidOperationException(
                                       $"Event handler '{eventRegistration.HandlerType.FullName}' is not registered.");
                if (!GeneratedEventDispatchRegistry.TryGetHandler(
                        eventRegistration.EventType,
                        eventRegistration.HandlerType,
                        out var eventInvoker))
                {
                    if (!RuntimeFeature.IsDynamicCodeSupported)
                    {
                        throw MissingGeneratedEventDispatch(eventRegistration);
                    }

                    eventInvoker = _eventHandlers.GetOrAdd(
                        (eventRegistration.HandlerType, eventRegistration.EventType),
                        static key => CompileEventHandler(key.Item2));
                }

                await eventInvoker(eventHandler, context.Message!).ConfigureAwait(false);
                AnyProtocolDiagnostics.RecordHandler(
                    context,
                    eventRegistration.TransportName,
                    startedAt,
                    "success");
                return;
            }

            var registration = dispatchState.Registration ??
                               throw new InvalidOperationException("Server registration was not initialized.");
            var method = context.Method ??
                         throw new InvalidOperationException("Contract method was not initialized.");
            var implementation = invocation.Services!.Resolve(registration.ImplementationType) ??
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

                invocation.Result.StreamResult = streamHandler(
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
            invocation.Result.HandlerResult = result;

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

    private static InvalidOperationException MissingGeneratedDispatch(
        Type contractType,
        ContractMethodDescriptor method)
        => new(
            $"Native AOT server dispatch for '{contractType.FullName}.{method.MethodName}' has no " +
            "generated delegate. Reference AnyProtocol.Generator and register the closed contract " +
            "directly with LinkBuilder.AddServer<TContract, TImplementation>().");

    private static InvalidOperationException MissingGeneratedEventDispatch(
        EventRegistration registration)
        => new(
            $"Native AOT event dispatch for '{registration.EventType.FullName}' to " +
            $"'{registration.HandlerType.FullName}' has no generated delegate. Reference " +
            "AnyProtocol.Generator and register the closed pair directly with " +
            "LinkBuilder.AddEventHandler<TEvent, THandler>().");

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
    private static GeneratedEventHandler CompileEventHandler(Type eventType)
    {
        var consumerType = typeof(IEventConsumer<>).MakeGenericType(eventType);
        var consumeMethod = consumerType.GetMethod(nameof(IEventConsumer<object>.ConsumeAsync))!;
        var target = Expression.Parameter(typeof(object), "target");
        var message = Expression.Parameter(typeof(object), "message");
        var body = Expression.Call(
            Expression.Convert(target, consumerType),
            consumeMethod,
            Expression.Convert(message, eventType));
        return Expression.Lambda<GeneratedEventHandler>(body, target, message).Compile();
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
        var castMethod = typeof(HandlerInvoker)
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
            return typeof(HandlerInvoker)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(
                    method =>
                        method.Name == nameof(AwaitTaskAsync) &&
                        !method.IsGenericMethod);
        }

        if (returnType == typeof(ValueTask))
        {
            return typeof(HandlerInvoker)
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
        return typeof(HandlerInvoker).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
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

}
