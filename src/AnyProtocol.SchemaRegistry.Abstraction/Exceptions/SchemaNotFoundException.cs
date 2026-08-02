namespace AnyProtocol.SchemaRegistry.Abstraction.Exceptions;

/// <summary>
/// Represents an error raised when schema not found processing fails.
/// </summary>
public class SchemaNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the SchemaNotFoundException class.
    /// </summary>
    public SchemaNotFoundException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the SchemaNotFoundException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    public SchemaNotFoundException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SchemaNotFoundException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    /// <param name="inner">The inner.</param>
    public SchemaNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }
}