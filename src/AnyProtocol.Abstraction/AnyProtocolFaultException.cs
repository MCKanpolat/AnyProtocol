namespace AnyProtocol.Abstraction;

/// <summary>
/// Represents an error raised when anyprotocol fault processing fails.
/// </summary>
public class AnyProtocolFaultException : Exception
{
    /// <summary>
    /// Initializes a new instance of the AnyProtocolFaultException class.
    /// </summary>
    /// <param name="fault">The fault.</param>
    public AnyProtocolFaultException(FaultMessage fault)
        : base(fault.Message)
    {
        Fault = fault;
    }

    /// <summary>
    /// Gets the fault.
    /// </summary>
    /// <value>The fault.</value>
    public FaultMessage Fault { get; }
}

/// <summary>
/// Represents an error raised when anyprotocol validation processing fails.
/// </summary>
public sealed class AnyProtocolValidationException : AnyProtocolFaultException
{
    /// <summary>
    /// Initializes a new instance of the AnyProtocolValidationException class.
    /// </summary>
    /// <param name="fault">The fault.</param>
    public AnyProtocolValidationException(FaultMessage fault)
        : base(fault)
    {
    }
}
