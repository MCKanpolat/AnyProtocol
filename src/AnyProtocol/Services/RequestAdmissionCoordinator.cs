using AnyProtocol.Abstraction;

namespace AnyProtocol.Services;

/// <summary>
/// Provides a race-safe active-work admission coordinator.
/// </summary>
public sealed class RequestAdmissionCoordinator : IRequestAdmission
{
    private readonly object _gate = new();
    private TaskCompletionSource _idle = CreateCompletedSource();
    // Standalone ingress adapters (for example McpToolInvoker) may be used
    // without constructing an AnyProtocolBus. The bus explicitly moves the
    // shared coordinator to draining during construction until StartAsync.
    private bool _draining;
    private int _activeCount;

    /// <inheritdoc />
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <inheritdoc />
    public bool IsDraining
    {
        get
        {
            lock (_gate)
            {
                return _draining;
            }
        }
    }

    /// <inheritdoc />
    public void StartAccepting()
    {
        lock (_gate)
        {
            if (_activeCount != 0)
            {
                throw new InvalidOperationException("Cannot start admission while work is active.");
            }

            _draining = false;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <inheritdoc />
    public void BeginDrain()
    {
        lock (_gate)
        {
            _draining = true;
            if (_activeCount == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public IAdmissionLease? TryEnter()
    {
        lock (_gate)
        {
            if (_draining)
            {
                return null;
            }

            _activeCount++;
            AnyProtocolDiagnostics.RecordActiveWork(1);
            return new AdmissionLease(this);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task idle;
        lock (_gate)
        {
            if (_activeCount == 0)
            {
                return true;
            }

            idle = _idle.Task;
        }

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Exit()
    {
        lock (_gate)
        {
            if (_activeCount == 0)
            {
                throw new InvalidOperationException("An admission lease was released more than once.");
            }

            _activeCount--;
            AnyProtocolDiagnostics.RecordActiveWork(-1);
            if (_activeCount == 0 && _draining)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }

    private sealed class AdmissionLease(RequestAdmissionCoordinator owner) : IAdmissionLease
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
