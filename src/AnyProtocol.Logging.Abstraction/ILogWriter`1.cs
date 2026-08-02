namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Defines operations for log writer.
/// </summary>
/// <typeparam name="TCategoryName">The category name type.</typeparam>
public interface ILogWriter<out TCategoryName> : ILogWriter
{
}