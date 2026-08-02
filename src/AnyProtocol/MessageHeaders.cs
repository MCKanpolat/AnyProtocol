using System.Collections;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides case-insensitive access to message metadata headers.
/// </summary>
public sealed class MessageHeaders : IMessageHeaders
{
    private readonly Dictionary<string, string> _headers;

    /// <summary>
    /// Initializes a new instance of the MessageHeaders class.
    /// </summary>
    public MessageHeaders()
        : this([])
    {
    }

    /// <summary>
    /// Initializes a new instance of the MessageHeaders class.
    /// </summary>
    /// <param name="headers">The headers.</param>
    public MessageHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Performs the this[string] operation.
    /// </summary>
    public string? this[string key]
    {
        get => _headers.GetValueOrDefault(key);
        set
        {
            if (value is null)
            {
                _headers.Remove(key);
                return;
            }

            _headers[key] = value;
        }
    }

    string IReadOnlyDictionary<string, string>.this[string key] => _headers[key];

    /// <summary>
    /// Gets the keys.
    /// </summary>
    /// <value>The keys.</value>
    public IEnumerable<string> Keys => _headers.Keys;

    /// <summary>
    /// Gets the values.
    /// </summary>
    /// <value>The values.</value>
    public IEnumerable<string> Values => _headers.Values;

    /// <summary>
    /// Gets the count.
    /// </summary>
    /// <value>The count.</value>
    public int Count => _headers.Count;

    /// <summary>
    /// Performs the set operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="value">The value to store.</param>
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _headers[key] = value;
    }

    /// <summary>
    /// Performs the remove operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool Remove(string key) => _headers.Remove(key);

    /// <summary>
    /// Performs the contains key operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool ContainsKey(string key) => _headers.ContainsKey(key);

    /// <summary>
    /// Attempts to get value.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="value">The value to store.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool TryGetValue(string key, out string value) => _headers.TryGetValue(key, out value!);

    /// <summary>
    /// Gets enumerator.
    /// </summary>
    /// <returns>The result of the get enumerator operation.</returns>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}