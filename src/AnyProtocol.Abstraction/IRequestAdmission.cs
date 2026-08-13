namespace AnyProtocol.Abstraction;

/// <summary>
/// Coordinates admission of work during an application drain.
/// </summary>
public interface IRequestAdmission
{
    /// <summary>
    /// Gets the number of admitted operations that have not exited yet.
    /// </summary>
    int ActiveCount { get; }

    /// <summary>
    /// Gets a value indicating whether new work is being rejected.
    /// </summary>
    bool IsDraining { get; }

    /// <summary>
    /// Starts a new accepting lifecycle.
    /// </summary>
    void StartAccepting();

    /// <summary>
    /// Atomically prevents new work from entering and returns the drain linearization point.
    /// </summary>
    void BeginDrain();

    /// <summary>
    /// Attempts to admit work.
    /// </summary>
    /// <returns>A lease when admitted; otherwise <see langword="null"/>.</returns>
    IAdmissionLease? TryEnter();

    /// <summary>
    /// Waits until all admitted work has exited or the timeout expires.
    /// </summary>
    /// <param name="timeout">The maximum wait duration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the coordinator became idle.</returns>
    ValueTask<bool> WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents one successful admission into active work.
/// </summary>
public interface IAdmissionLease : IDisposable
{
}
