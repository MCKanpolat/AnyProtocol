namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for client invoker.
/// </summary>
public interface IClientInvoker
{
    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a transport envelope to the specified logical channel.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask SendAsync<TRequest>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request and asynchronously yields the streamed response items.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    IAsyncEnumerable<TItem> StreamAsync<TRequest, TItem>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default);
}
