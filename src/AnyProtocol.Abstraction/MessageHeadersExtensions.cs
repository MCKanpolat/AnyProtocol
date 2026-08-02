using System.Globalization;

namespace AnyProtocol.Abstraction;

/// <summary>
/// Provides typed accessors for standard AnyProtocol message headers.
/// </summary>
public static class MessageHeadersExtensions
{
    /// <summary>
    /// Attempts to get&lt;t&gt;.
    /// </summary>
    /// <typeparam name="T">The header value type.</typeparam>
    /// <param name="headers">The headers.</param>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool TryGet<T>(this IMessageHeaders headers, string key, out T? value)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!headers.TryGetValue(key, out var raw))
        {
            value = default;
            return false;
        }

        try
        {
            if (typeof(T).IsEnum)
            {
                value = (T)Enum.Parse(typeof(T), raw, ignoreCase: true);
            }
            else if (typeof(T) == typeof(Guid))
            {
                value = (T)(object)Guid.Parse(raw);
            }
            else if (typeof(T) == typeof(DateTimeOffset))
            {
                value = (T)(object)DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            else
            {
                value = (T?)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Gets &lt;t&gt;.
    /// </summary>
    /// <typeparam name="T">The header value type.</typeparam>
    /// <param name="headers">The headers.</param>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="defaultValue">The default value.</param>
    /// <returns>The result of the get&lt;t&gt; operation.</returns>
    public static T Get<T>(this IMessageHeaders headers, string key, T defaultValue = default!)
        => headers.TryGet<T>(key, out var value) ? value! : defaultValue;

    /// <summary>
    /// Performs the set&lt;t&gt; operation.
    /// </summary>
    /// <typeparam name="T">The header value type.</typeparam>
    /// <param name="headers">The headers.</param>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="value">The value to store.</param>
    public static void Set<T>(this IMessageHeaders headers, string key, T value)
        where T : notnull
    {
        var text = value switch
        {
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        headers.Set(key, text ?? string.Empty);
    }
}
