namespace AnyProtocol.Protocol.Rest;

/// <summary>
/// Overrides the HTTP method used by the optional ASP.NET Core REST operation endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RestHttpMethodAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RestHttpMethodAttribute"/> class.
    /// </summary>
    /// <param name="method">The HTTP method, such as <c>GET</c> or <c>POST</c>.</param>
    public RestHttpMethodAttribute(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var normalized = method.Trim().ToUpperInvariant();
        if (normalized.Length == 0 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                !"!#$%&'*+-.^_`|~".Contains(character)))
        {
            throw new ArgumentException(
                $"The HTTP method '{method}' is not a valid HTTP method token.",
                nameof(method));
        }

        Method = normalized;
    }

    /// <summary>
    /// Gets the HTTP method.
    /// </summary>
    /// <value>The HTTP method.</value>
    public string Method { get; }
}
