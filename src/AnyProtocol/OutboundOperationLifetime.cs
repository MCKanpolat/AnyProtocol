namespace AnyProtocol;

/// <summary>
/// Owns the cancellation boundary for outbound operations started during one bus run.
/// </summary>
public sealed class OutboundOperationLifetime : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _runCancellation = new();
    private bool _disposed;

    /// <summary>
    /// Gets the cancellation token for the current bus run.
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _runCancellation.Token;
            }
        }
    }

    internal void StartRun()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runCancellation.Dispose();
            _runCancellation = new CancellationTokenSource();
        }
    }

    internal void CancelRun()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _runCancellation.Cancel();
        }
    }

    /// <summary>Forces cancellation and releases the current run token source.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runCancellation.Cancel();
            _runCancellation.Dispose();
        }
    }
}
