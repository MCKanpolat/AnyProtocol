namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Provides extension methods for log writer factory configuration and registration.
/// </summary>
public static class LogWriterFactoryExtensions
{
    /// <summary>
    /// Creates log writer.
    /// </summary>
    /// <param name="factory">The factory used to create the instance.</param>
    /// <param name="type">The runtime type to resolve.</param>
    /// <returns>The result of the create log writer operation.</returns>
    public static ILogWriter CreateLogWriter(this ILogWriterFactory factory, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return factory.CreateLogWriter(type.FullName!);
    }

    /// <summary>
    /// Creates log writer&lt;t&gt;.
    /// </summary>
    /// <typeparam name="T">The logging category type.</typeparam>
    /// <param name="factory">The factory used to create the instance.</param>
    /// <returns>The result of the create log writer&lt;t&gt; operation.</returns>
    public static ILogWriter CreateLogWriter<T>(this ILogWriterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return factory.CreateLogWriter(typeof(T).FullName!);
    }
}