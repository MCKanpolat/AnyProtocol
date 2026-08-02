using AnyProtocol.Abstraction;

namespace OrderSystem.Contracts;

[Channel("orders")]
[FaultContract(typeof(OrderFault))]
public interface IOrderService
{
    [McpTool(Name = "orders_create", Description = "Creates a new order.")]
    ValueTask<OrderResponse> CreateAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken);

    [Idempotent]
    [McpTool(
        Name = "orders_get",
        Description = "Gets an order by its identifier.",
        ReadOnly = true,
        Idempotent = true)]
    ValueTask<OrderResponse> GetAsync(
        GetOrderRequest request,
        CancellationToken cancellationToken);

    [Idempotent]
    [McpTool(
        Name = "orders_list",
        Description = "Lists all current orders.",
        ReadOnly = true,
        Idempotent = true)]
    ValueTask<OrderListResponse> ListAsync(
        ListOrdersRequest request,
        CancellationToken cancellationToken);
}
