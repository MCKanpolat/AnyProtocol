using System.Collections.Concurrent;
using OrderSystem.Contracts;

namespace OrderSystem.Server.Repositories;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<string, OrderResponse> orders = new();
    private int sequence = 1000;

    public InMemoryOrderRepository()
    {
        var seed = new OrderResponse("ORD-1000", "seed-customer", 100m, "ready");
        orders[seed.OrderId] = seed;
    }

    public OrderResponse Create(CreateOrderRequest request)
    {
        var orderId = $"ORD-{Interlocked.Increment(ref sequence)}";
        var order = new OrderResponse(orderId, request.CustomerId, request.Total, "ready");
        orders[orderId] = order;
        return order;
    }

    public OrderResponse? Get(string orderId)
        => orders.TryGetValue(orderId, out var order) ? order : null;

    public IReadOnlyList<OrderResponse> List()
        => orders.Values.OrderBy(static order => order.OrderId, StringComparer.Ordinal).ToArray();
}
