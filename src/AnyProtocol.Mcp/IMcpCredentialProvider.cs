namespace AnyProtocol.Mcp;

/// <summary>
/// Defines operations for mcp credential.
/// </summary>
public interface IMcpCredentialProvider
{
    /// <summary>
    /// Gets auth token.
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    string? GetAuthToken();
}

/// <summary>
/// Provides empty mcp credential values to AnyProtocol operations.
/// </summary>
public sealed class EmptyMcpCredentialProvider : IMcpCredentialProvider
{
    /// <summary>
    /// Gets auth token.
    /// </summary>
    /// <returns>The result of the get auth token operation.</returns>
    public string? GetAuthToken() => null;
}
