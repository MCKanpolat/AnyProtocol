namespace AnyProtocol.Storage.Abstraction.Exceptions;

/// <summary>
/// Represents an error raised when message not found processing fails.
/// </summary>
public class MessageNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the MessageNotFoundException class.
    /// </summary>
    public MessageNotFoundException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the MessageNotFoundException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    public MessageNotFoundException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the MessageNotFoundException class.
    /// </summary>
    /// <param name="message">The message payload or description.</param>
    /// <param name="inner">The inner.</param>
    public MessageNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }
}