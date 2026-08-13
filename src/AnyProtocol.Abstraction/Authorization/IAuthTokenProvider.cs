namespace AnyProtocol.Authorization.Abstraction;

/// <summary>Supplies the credential propagated on an outbound AnyProtocol message.</summary>
public interface IAuthTokenProvider
{
    /// <summary>Gets the current credential, or <see langword="null"/> to omit it.</summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}
