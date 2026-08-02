using AnyProtocol.Abstraction;
using OrderSystem.Contracts;
using OrderSystem.Server.Repositories;

namespace OrderSystem.Server.Services;

public sealed class OrderService(IOrderRepository repository) : IOrderService
{
    public ValueTask<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.CustomerId) || request.Total <= 0)
        {
            throw new AnyProtocolFaultException(
                new FaultMessage("invalid_order", "Customer ID and a positive total are required."));
        }

        return ValueTask.FromResult(repository.Create(request));
    }

    public ValueTask<OrderResponse> GetAsync(
        GetOrderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return repository.Get(request.OrderId) is { } order
            ? ValueTask.FromResult(order)
            : throw new AnyProtocolFaultException(
                new FaultMessage("order_not_found", "The requested order was not found."));
    }

    public ValueTask<OrderListResponse> ListAsync(
        ListOrdersRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new OrderListResponse(repository.List()));
    }
}
