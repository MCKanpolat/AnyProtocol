namespace AnyProtocol.Authorization.Abstraction;

/// <summary>Evaluates the permissions required by an inbound contract operation.</summary>
public interface IAuthorizationProvider
{
    /// <summary>Returns whether the current caller has every required permission.</summary>
    ValueTask<bool> CheckPermissionsAsync(
        IEnumerable<string> permissions,
        CancellationToken cancellationToken = default);
}
