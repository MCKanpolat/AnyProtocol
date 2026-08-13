namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Signals that a subscription callback lost the shutdown admission race.
/// Durable transports should leave the message uncommitted or negatively acknowledge it for
/// redelivery rather than treating it as a handler failure.
/// </summary>
public sealed class MessageAdmissionRejectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageAdmissionRejectedException"/> class.
    /// </summary>
    public MessageAdmissionRejectedException()
        : base("The message callback was rejected because AnyProtocol is draining.")
    {
    }
}
