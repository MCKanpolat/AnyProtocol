namespace AnyProtocol.Serializer.Abstraction.Exceptions;

/// <summary>
/// Represents an error raised when serialization failed processing fails.
/// </summary>
public class SerializationFailedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the SerializationFailedException class.
    /// </summary>
    public SerializationFailedException()
    { }

    /// <summary>
    /// Initializes a new instance of the SerializationFailedException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    public SerializationFailedException(string message) : base(message)
    { }

    /// <summary>
    /// Initializes a new instance of the SerializationFailedException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    /// <param name="inner">The inner.</param>
    public SerializationFailedException(string message, Exception inner) : base(message, inner)
    { }
}