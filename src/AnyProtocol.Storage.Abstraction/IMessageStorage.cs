namespace AnyProtocol.Storage.Abstraction;

/// <summary>
/// Defines operations for message storage.
/// </summary>
public interface IMessageStorage : IDisposable
{
    /// <summary>
    /// Gets async.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the get async.</returns>
    public ValueTask<ReadOnlyMemory<byte>> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the set async operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}