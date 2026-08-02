using OrderSystem.Contracts;

namespace OrderSystem.Server.Repositories;

public interface IOrderRepository
{
    OrderResponse Create(CreateOrderRequest request);

    OrderResponse? Get(string orderId);

    IReadOnlyList<OrderResponse> List();
}
