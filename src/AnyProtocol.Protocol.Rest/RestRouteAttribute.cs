namespace AnyProtocol.Protocol.Rest;

/// <summary>
/// Overrides the operation route used by the optional ASP.NET Core REST facade.
/// </summary>
/// <param name="template">
/// The route segment or relative route. On a contract it is used as the route prefix;
/// on a method it is used as the operation route segment.
/// </param>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
public sealed class RestRouteAttribute(string template) : Attribute
{
    /// <summary>
    /// Gets the route template.
    /// </summary>
    /// <value>The route template.</value>
    public string Template { get; } = template;
}
