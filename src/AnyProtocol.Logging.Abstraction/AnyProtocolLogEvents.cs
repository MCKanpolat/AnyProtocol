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
    /// <summary>
    /// The unhandled message error value.
    /// </summary>
    public const int UnhandledMessageError = 1600;
    /// <summary>
    /// The fault delivery failed value.
    /// </summary>
    public const int FaultDeliveryFailed = 1601;
    /// <summary>
    /// The error handler failed value.
    /// </summary>
    public const int ErrorHandlerFailed = 1602;
    /// <summary>
    /// The routing failure value.
    /// </summary>
    public const int RoutingFailed = 1603;
    /// <summary>
    /// The stream delivery failure value.
    /// </summary>
    public const int StreamDeliveryFailed = 1604;
    /// <summary>
    /// The forced shutdown cancellation value.
    /// </summary>
    public const int ForcedShutdownCancellation = 1605;
    /// <summary>
    /// The ZeroMQ socket loop failed value.
    /// </summary>
    public const int ZeroMqSocketLoopFailed = 1700;
    /// <summary>
    /// The ZeroMQ decode failed value.
    /// </summary>
    public const int ZeroMqDecodeFailed = 1701;
    /// <summary>
    /// The ZeroMQ subscription handler failed value.
    /// </summary>
    public const int ZeroMqHandlerFailed = 1702;
    /// <summary>
    /// The ZeroMQ pending outbound failure value.
    /// </summary>
    public const int ZeroMqPendingOutboundFailed = 1703;
}
