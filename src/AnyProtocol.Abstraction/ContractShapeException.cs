namespace AnyProtocol.Abstraction;

/// <summary>
/// Represents an error raised when contract shape processing fails.
/// </summary>
public sealed class ContractShapeException : Exception
{
    /// <summary>
    /// Initializes a new instance of the ContractShapeException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    public ContractShapeException(string message)
        : base(message)
    {
    }
}
