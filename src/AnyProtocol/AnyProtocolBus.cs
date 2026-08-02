using System.Runtime.ExceptionServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Coordinates message publishing, request/reply calls, subscriptions, and bus lifecycle operations.
/// </summary>
public sealed class AnyProtocolBus : IAnyProtocolBus, IAsyncDisposable
{
    private readonly LinkConfiguration _configuration;
    private readonly ContractDescriptorFactory _descriptorFactory;
    private readonly TransportRegistry _transports;
    private readonly MessageDispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly List<IAsyncDisposable> _subscriptions = [];
    private CancellationTokenSource? _runCancellation;
    private int _state = (int)AnyProtocolBusState.Created;

    /// <summary>
    /// Initializes a new instance of the AnyProtocolBus class.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    /// <param name="transports">The transports.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    public AnyProtocolBus(
        LinkConfiguration configuration,
        ContractDescriptorFactory descriptorFactory,
        TransportRegistry transports,
        MessageDispatcher dispatcher)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _descriptorFactory = descriptorFactory ?? throw new ArgumentNullException(nameof(descriptorFactory));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Gets a value indicating whether is started applies.
    /// </summary>
    /// <value>true when is started applies; otherwise, false.</value>
    public bool IsStarted => State == AnyProtocolBusState.Started;

    /// <summary>
    /// Gets the current state of the component.
    /// </summary>
    /// <value>The state.</value>
    public AnyProtocolBusState State =>
        (AnyProtocolBusState)Volatile.Read(ref _state);

    /// <summary>
    /// Performs the start async operation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(State == AnyProtocolBusState.Disposed, this);
            if (State == AnyProtocolBusState.Started)
            {
                return;
            }

            if (State is not (AnyProtocolBusState.Created or AnyProtocolBusState.Stopped))
            {
                throw new InvalidOperationException($"Cannot start AnyProtocol while it is {State}.");
            }

            Volatile.Write(ref _state, (int)AnyProtocolBusState.Starting);
            var runCancellation = new CancellationTokenSource();
            var startedSubscriptions = new List<IAsyncDisposable>();
            try
            {
                await StartSubscriptionsAsync(
                        startedSubscriptions,
                        runCancellation.Token,
                        cancellationToken)
                    .ConfigureAwait(false);
                _subscriptions.AddRange(startedSubscriptions);
                _runCancellation = runCancellation;
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Started);
            }
            catch (Exception startupException)
            {
                runCancellation.Cancel();
                var rollbackException = await DisposeSubscriptionsAsync(startedSubscriptions)
                    .ConfigureAwait(false);
                if (rollbackException is not null)
                {
                    _subscriptions.AddRange(startedSubscriptions);
                    _runCancellation = runCancellation;
                    Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopping);
                    throw new AggregateException(
                        "AnyProtocol startup failed and subscription rollback also failed.",
                        startupException,
                        rollbackException);
                }

                runCancellation.Dispose();
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopped);
                ExceptionDispatchInfo.Capture(startupException).Throw();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Performs the stop async operation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == AnyProtocolBusState.Disposed)
            {
                return;
            }

            if (State is AnyProtocolBusState.Created or AnyProtocolBusState.Stopped)
            {
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopped);
                return;
            }

            if (State is not (AnyProtocolBusState.Started or AnyProtocolBusState.Stopping))
            {
                throw new InvalidOperationException($"Cannot stop AnyProtocol while it is {State}.");
            }

            if (State == AnyProtocolBusState.Started)
            {
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopping);
                _runCancellation?.Cancel();
            }

            var disposalException = await DisposeSubscriptionsAsync(_subscriptions)
                .ConfigureAwait(false);
            if (disposalException is not null)
            {
                throw disposalException;
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopped);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == AnyProtocolBusState.Disposed)
            {
                return;
            }

            Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopping);
            _runCancellation?.Cancel();
            var exceptions = new List<Exception>();
            var subscriptionException = await DisposeSubscriptionsAsync(_subscriptions)
                .ConfigureAwait(false);
            if (subscriptionException is not null)
            {
                exceptions.Add(subscriptionException);
            }

            var transportException = await DisposeAllAsync(_transports.All).ConfigureAwait(false);
            if (transportException is not null)
            {
                exceptions.Add(transportException);
            }

            if (exceptions.Count != 0)
            {
                throw new AggregateException("AnyProtocol disposal failed.", exceptions);
            }

            _runCancellation?.Dispose();
            _runCancellation = null;
            Volatile.Write(ref _state, (int)AnyProtocolBusState.Disposed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async ValueTask StartSubscriptionsAsync(
        ICollection<IAsyncDisposable> subscriptions,
        CancellationToken runCancellation,
        CancellationToken startupCancellation)
    {
        var routes = _configuration.Servers
            .SelectMany(
                registration => registration.Protocols
                    .Where(protocol => protocol != ProtocolKey.Mcp)
                    .Where(protocol =>
                        _transports.GetRequired(protocol) is not INativeServerTransport)
                    .SelectMany(
                        protocol => _descriptorFactory.Create(registration.ContractType).Methods
                            .Select(method => new Route(registration, method, protocol))))
            .GroupBy(route => (route.Protocol, route.Method.Channel));

        foreach (var routeGroup in routes)
        {
            var transport = _transports.GetRequired(routeGroup.Key.Protocol);
            var routeTable = routeGroup.ToArray();
            var subscription = await transport.SubscribeAsync(
                    routeGroup.Key.Channel,
                    async (envelope, token) =>
                    {
                        using var linkedCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(token, runCancellation);
                        var dispatchToken = linkedCancellation.Token;
                        var contractName = envelope.Headers[HeaderNames.Contract];
                        var methodName = envelope.Headers[HeaderNames.Method];
                        var route = routeTable.SingleOrDefault(
                            candidate =>
                                candidate.Method.ContractName == contractName &&
                                candidate.Method.MethodName == methodName);
                        if (route is null)
                        {
                            await _dispatcher.DispatchRoutingFaultAsync(
                                    envelope,
                                    transport,
                                    $"No handler is registered for '{contractName}.{methodName}'.",
                                    dispatchToken)
                                .ConfigureAwait(false);
                            return;
                        }

                        if (route.Method.Operation == ContractOperation.Stream)
                        {
                            var replyTo = envelope.Headers[HeaderNames.ReplyTo];
                            if (string.IsNullOrWhiteSpace(replyTo))
                            {
                                System.Diagnostics.Trace.TraceError(
                                    "Ignored streaming request '{0}.{1}' without a reply channel.",
                                    route.Method.ContractName,
                                    route.Method.MethodName);
                                return;
                            }

                            try
                            {
                                await foreach (var response in _dispatcher.DispatchStreamAsync(
                                                       route.Registration,
                                                       route.Method,
                                                       envelope,
                                                       transport,
                                                       dispatchToken,
                                                       route.Protocol)
                                                   .ConfigureAwait(false))
                                {
                                    await transport.SendAsync(replyTo, response, dispatchToken)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException)
                                when (token.IsCancellationRequested ||
                                      runCancellation.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                System.Diagnostics.Trace.TraceError(
                                    "Failed to deliver stream '{0}.{1}'; exception type '{2}'.",
                                    route.Method.ContractName,
                                    route.Method.MethodName,
                                    exception.GetType().FullName);
                            }

                            return;
                        }

                        await _dispatcher.DispatchAsync(
                                route.Registration,
                                route.Method,
                                envelope,
                                transport,
                                dispatchToken,
                                route.Protocol)
                            .ConfigureAwait(false);
                    },
                    new SubscriptionOptions
                    {
                        ConsumerGroup = $"anyprotocol.rpc.{routeGroup.Key.Channel}"
                    },
                    startupCancellation)
                .ConfigureAwait(false);
            subscriptions.Add(subscription);
        }

        foreach (var registration in _configuration.Events)
        {
            var transport = _transports.GetRequired(registration.TransportName);
            if (transport is INativeServerTransport)
            {
                continue;
            }

            var subscription = await transport.SubscribeAsync(
                    registration.Channel,
                    (envelope, token) =>
                    {
                        var linkedCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(token, runCancellation);
                        return DispatchEventAsync(
                            registration,
                            envelope,
                            transport,
                            linkedCancellation);
                    },
                    new SubscriptionOptions
                    {
                        ConsumerGroup = registration.ConsumerGroup is null
                            ? null
                            : $"{registration.ConsumerGroup}:{registration.EventType.FullName}"
                    },
                    startupCancellation)
                .ConfigureAwait(false);
            subscriptions.Add(subscription);
        }
    }

    private async ValueTask DispatchEventAsync(
        EventRegistration registration,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationTokenSource linkedCancellation)
    {
        using (linkedCancellation)
        {
            var messageType = envelope.Headers.Get(
                HeaderNames.MessageType,
                MessageType.Event);
            var contractName = envelope.Headers[HeaderNames.Contract];
            var expectedContract =
                registration.EventType.FullName ?? registration.EventType.Name;
            if (messageType != MessageType.Event ||
                !string.Equals(contractName, expectedContract, StringComparison.Ordinal))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Ignored unmatched AnyProtocol event on channel '{0}'.",
                    registration.Channel);
                return;
            }

            await _dispatcher.DispatchEventAsync(
                    registration,
                    envelope,
                    transport,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<Exception?> DisposeAllAsync(
        IEnumerable<IAsyncDisposable> resources)
    {
        List<Exception>? exceptions = null;
        foreach (var resource in resources.Reverse().ToArray())
        {
            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return exceptions switch
        {
            null => null,
            [var single] => single,
            _ => new AggregateException(exceptions)
        };
    }

    private static async ValueTask<Exception?> DisposeSubscriptionsAsync(
        List<IAsyncDisposable> subscriptions)
    {
        List<Exception>? exceptions = null;
        for (var index = subscriptions.Count - 1; index >= 0; index--)
        {
            try
            {
                await subscriptions[index].DisposeAsync().ConfigureAwait(false);
                subscriptions.RemoveAt(index);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return exceptions switch
        {
            null => null,
            [var single] => single,
            _ => new AggregateException(exceptions)
        };
    }

    private sealed record Route(
        ServerRegistration Registration,
        ContractMethodDescriptor Method,
        ProtocolKey Protocol);
}
