namespace AnyProtocol.Abstraction;

/// <summary>
/// Represents the next asynchronous stage in the message-processing pipeline.
/// </summary>
/// <param name="context">The context for the current operation.</param>
/// <returns>A task that represents completion of the pipeline stage.</returns>
public delegate ValueTask MessageFilterDelegate(IMessageContext context);
