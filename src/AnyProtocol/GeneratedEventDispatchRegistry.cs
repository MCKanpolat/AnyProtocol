namespace AnyProtocol;

/// <summary>
/// Stores source-generated event consumer dispatch delegates for Native AOT execution.
/// </summary>
public static class GeneratedEventDispatchRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(Type EventType, Type HandlerType), GeneratedEventHandler>
        Handlers = [];

    /// <summary>
    /// Registers a source-generated event consumer dispatch delegate.
    /// </summary>
    /// <param name="eventType">The event message type.</param>
    /// <param name="handlerType">The event consumer type.</param>
    /// <param name="handler">The generated dispatch delegate.</param>
    public static void Register(
        Type eventType,
        Type handlerType,
        GeneratedEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(handler);

        lock (Sync)
        {
            var key = (eventType, handlerType);
            if (Handlers.TryGetValue(key, out var existing))
            {
                if (existing == handler)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Generated event dispatch for '{DisplayName(eventType)}' and " +
                    $"'{DisplayName(handlerType)}' is already registered with a different delegate.");
            }

            Handlers.Add(key, handler);
        }
    }

    /// <summary>
    /// Gets whether source-generated dispatch exists for an event consumer pair.
    /// </summary>
    /// <param name="eventType">The event message type.</param>
    /// <param name="handlerType">The event consumer type.</param>
    /// <returns>true when a generated delegate is available; otherwise, false.</returns>
    public static bool IsRegistered(Type eventType, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlerType);
        lock (Sync)
        {
            return Handlers.ContainsKey((eventType, handlerType));
        }
    }

    internal static bool TryGetHandler(
        Type eventType,
        Type handlerType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratedEventHandler? handler)
    {
        lock (Sync)
        {
            return Handlers.TryGetValue((eventType, handlerType), out handler);
        }
    }

    private static string DisplayName(Type type) => type.FullName ?? type.Name;
}

/// <summary>
/// Invokes a generated event consumer.
/// </summary>
/// <param name="target">The event consumer instance.</param>
/// <param name="message">The event message.</param>
/// <returns>A task that represents the event handling operation.</returns>
public delegate ValueTask GeneratedEventHandler(object target, object message);
