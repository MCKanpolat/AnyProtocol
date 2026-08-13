using System.Runtime.ExceptionServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Coordinates inbound subscriptions and bus lifecycle operations.
/// </summary>
public sealed class AnyProtocolBus : IAnyProtocolBus, IAsyncDisposable
{
    private readonly TransportRegistry _transports;
    private readonly InboundSubscriptionHost _subscriptionHost;
    private readonly IRequestAdmission _admission;
    private readonly OutboundOperationLifetime _outboundLifetime;
    private readonly ShutdownOptions _shutdownOptions;
    private readonly ILogWriter _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private int _state = (int)AnyProtocolBusState.Created;

    /// <summary>
    /// Initializes a new instance of the AnyProtocolBus class.
    /// </summary>
    /// <param name="runtimePlan">The immutable runtime plan.</param>
    /// <param name="transports">The transports.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="admission">The shared request admission coordinator.</param>
    /// <param name="shutdownOptions">The bounded shutdown policy.</param>
    /// <param name="logWriterFactory">The logging writer factory.</param>
    /// <param name="outboundLifetime">The cancellation boundary for outbound operations.</param>
    public AnyProtocolBus(
        RuntimePlan runtimePlan,
        TransportRegistry transports,
        MessageDispatcher dispatcher,
        IRequestAdmission? admission = null,
        ShutdownOptions? shutdownOptions = null,
        ILogWriterFactory? logWriterFactory = null,
        OutboundOperationLifetime? outboundLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _admission = admission ?? new Services.RequestAdmissionCoordinator();
        _outboundLifetime = outboundLifetime ?? new OutboundOperationLifetime();
        _shutdownOptions = shutdownOptions ?? new ShutdownOptions();
        _shutdownOptions.Validate();
        _logger = (logWriterFactory ?? NullLogWriterFactory.Instance)
            .CreateLogWriter(nameof(AnyProtocolBus));
        _subscriptionHost = new InboundSubscriptionHost(
            new InboundRouteTable(runtimePlan, transports),
            new InboundMessageRouter(dispatcher, _admission, _logger));
        _admission.BeginDrain();
    }

    /// <summary>
    /// Gets a value indicating whether the bus is started.
    /// </summary>
    /// <value>true when the bus is started; otherwise, false.</value>
    public bool IsStarted => State == AnyProtocolBusState.Started;

    /// <summary>
    /// Gets the current state of the component.
    /// </summary>
    /// <value>The state.</value>
    public AnyProtocolBusState State =>
        (AnyProtocolBusState)Volatile.Read(ref _state);

    /// <summary>
    /// Starts inbound transport subscriptions.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel startup.</param>
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
            try
            {
                _outboundLifetime.StartRun();
                _admission.StartAccepting();
                await _subscriptionHost.StartAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Started);
            }
            catch
            {
                _admission.BeginDrain();
                _outboundLifetime.CancelRun();
                Volatile.Write(
                    ref _state,
                    (int)(_subscriptionHost.HasActiveRun
                        ? AnyProtocolBusState.Stopping
                        : AnyProtocolBusState.Stopped));
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops accepting messages and drains inbound work.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the graceful drain.</param>
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

            if (State is not (
                AnyProtocolBusState.Started or
                AnyProtocolBusState.Draining or
                AnyProtocolBusState.Stopping))
            {
                throw new InvalidOperationException($"Cannot stop AnyProtocol while it is {State}.");
            }

            Exception? stopAcceptingException = null;
            OperationCanceledException? callerCancellation = null;
            if (State is AnyProtocolBusState.Started or AnyProtocolBusState.Draining)
            {
                Volatile.Write(ref _state, (int)AnyProtocolBusState.Draining);
                _admission.BeginDrain();
                stopAcceptingException = await _subscriptionHost.StopAcceptingAsync()
                    .ConfigureAwait(false);
                var drained = false;
                try
                {
                    drained = await _admission.WaitForIdleAsync(
                            _shutdownOptions.DrainTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    callerCancellation = exception;
                    drained = false;
                }

                if (!drained)
                {
                    _logger.LogEvent(
                        AnyProtocolLogEvents.ForcedShutdownCancellation,
                        LogSeverity.Warning,
                        "Forced shutdown cancellation started with {0} active operations.",
                        null,
                        _admission.ActiveCount);
                    AnyProtocolDiagnostics.RecordForcedShutdownCancellation(_admission.ActiveCount);
                    _subscriptionHost.CancelRun();
                    _outboundLifetime.CancelRun();
                    drained = await _admission.WaitForIdleAsync(
                            _shutdownOptions.ForcedCancellationTimeout)
                        .ConfigureAwait(false);
                }

                Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopping);
                if (!drained)
                {
                    var timeoutException = new TimeoutException(
                        $"AnyProtocol still has {_admission.ActiveCount} active operation(s) after " +
                        $"the forced-cancellation timeout of " +
                        $"{_shutdownOptions.ForcedCancellationTimeout}.",
                        callerCancellation);
                    if (stopAcceptingException is not null)
                    {
                        throw new AggregateException(
                            "Stopping AnyProtocol timed out after subscription quiesce failed.",
                            stopAcceptingException,
                            timeoutException);
                    }

                    throw timeoutException;
                }
            }
            else if (_admission.ActiveCount != 0)
            {
                _subscriptionHost.CancelRun();
                _outboundLifetime.CancelRun();
                var drained = await _admission.WaitForIdleAsync(
                        _shutdownOptions.ForcedCancellationTimeout)
                    .ConfigureAwait(false);
                if (!drained)
                {
                    throw new TimeoutException(
                        $"AnyProtocol still has {_admission.ActiveCount} active operation(s) after " +
                        $"the forced-cancellation timeout of " +
                        $"{_shutdownOptions.ForcedCancellationTimeout}.");
                }
            }

            var disposalException = await _subscriptionHost.DisposeSubscriptionsAsync()
                .ConfigureAwait(false);
            if (stopAcceptingException is not null && disposalException is not null)
            {
                throw new AggregateException(
                    "Stopping AnyProtocol failed while quiescing and disposing subscriptions.",
                    stopAcceptingException,
                    disposalException);
            }

            if (stopAcceptingException is not null)
            {
                throw stopAcceptingException;
            }

            if (disposalException is not null)
            {
                throw disposalException;
            }

            _subscriptionHost.CompleteRun();
            Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopped);
            if (callerCancellation is not null)
            {
                ExceptionDispatchInfo.Capture(callerCancellation).Throw();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops inbound work and releases subscriptions and transports.
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

            var exceptions = new List<Exception>();
            Volatile.Write(ref _state, (int)AnyProtocolBusState.Draining);
            _admission.BeginDrain();
            var stopAcceptingException = await _subscriptionHost.StopAcceptingAsync()
                .ConfigureAwait(false);
            _subscriptionHost.CancelRun();
            _outboundLifetime.CancelRun();
            if (stopAcceptingException is not null)
            {
                exceptions.Add(stopAcceptingException);
            }

            Volatile.Write(ref _state, (int)AnyProtocolBusState.Stopping);
            var drained = await _admission.WaitForIdleAsync(
                    _shutdownOptions.ForcedCancellationTimeout)
                .ConfigureAwait(false);
            if (!drained)
            {
                var timeoutException = new TimeoutException(
                    $"AnyProtocol still has {_admission.ActiveCount} active operation(s) after " +
                    $"the forced-cancellation timeout of " +
                    $"{_shutdownOptions.ForcedCancellationTimeout}.");
                if (exceptions.Count != 0)
                {
                    exceptions.Add(timeoutException);
                    throw new AggregateException("AnyProtocol disposal timed out.", exceptions);
                }

                throw timeoutException;
            }

            var subscriptionException = await _subscriptionHost.DisposeSubscriptionsAsync()
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

            _subscriptionHost.CompleteRun();
            _outboundLifetime.Dispose();
            Volatile.Write(ref _state, (int)AnyProtocolBusState.Disposed);
        }
        finally
        {
            _lifecycleLock.Release();
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
}
