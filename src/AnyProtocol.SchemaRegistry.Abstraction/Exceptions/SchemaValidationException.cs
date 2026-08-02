namespace AnyProtocol.SchemaRegistry.Abstraction.Exceptions;

/// <summary>
/// Represents an error raised when schema validation processing fails.
/// </summary>
public class SchemaValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the SchemaValidationException class.
    /// </summary>
    public SchemaValidationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the SchemaValidationException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    public SchemaValidationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SchemaValidationException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    /// <param name="inner">The inner.</param>
    public SchemaValidationException(string message, Exception inner) : base(message, inner)
    {
    }
}