namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Provides the anyprotocol log events implementation used by AnyProtocol applications.
/// </summary>
public static class AnyProtocolLogEvents
{
    /// <summary>
    /// The operation started value.
    /// </summary>
    public const int OperationStarted = 1000;
    /// <summary>
    /// The operation completed value.
    /// </summary>
    public const int OperationCompleted = 1001;
    /// <summary>
    /// The retry scheduled value.
    /// </summary>
    public const int RetryScheduled = 1100;
    /// <summary>
    /// The operation timed out value.
    /// </summary>
    public const int OperationTimedOut = 1200;
    /// <summary>
    /// The operation cancelled value.
    /// </summary>
    public const int OperationCancelled = 1201;
    /// <summary>
    /// The operation faulted value.
    /// </summary>
    public const int OperationFaulted = 1300;
    /// <summary>
    /// The dead lettered value.
    /// </summary>
    public const int DeadLettered = 1400;
    /// <summary>
    /// The readiness failed value.
    /// </summary>
    public const int ReadinessFailed = 1500;
}
