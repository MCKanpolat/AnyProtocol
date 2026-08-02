using AnyProtocol.Abstraction;
using OrderSystem.Contracts;
using OrderSystem.Server.Repositories;
using OrderSystem.Server.Services;

namespace OrderSystem.MultiProtocol.Tests;

public sealed class OrderServiceTests
{
    [Fact]
    public async Task Create_get_and_list_share_the_same_repository_state()
    {
        var service = new OrderService(new InMemoryOrderRepository());

        var created = await service.CreateAsync(
            new CreateOrderRequest("customer-42", 125.50m),
            CancellationToken.None);
        var fetched = await service.GetAsync(
            new GetOrderRequest(created.OrderId),
            CancellationToken.None);
        var listed = await service.ListAsync(new ListOrdersRequest(), CancellationToken.None);

        Assert.Equal("ORD-1001", created.OrderId);
        Assert.Equal(created, fetched);
        Assert.Contains(created, listed.Orders);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("customer-42", 0)]
    public async Task Invalid_create_returns_stable_fault(string customerId, decimal total)
    {
        var service = new OrderService(new InMemoryOrderRepository());

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await service.CreateAsync(
                new CreateOrderRequest(customerId, total),
                CancellationToken.None));

        Assert.Equal("invalid_order", exception.Fault.Code);
    }

    [Fact]
    public async Task Missing_order_returns_stable_fault()
    {
        var service = new OrderService(new InMemoryOrderRepository());

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await service.GetAsync(
                new GetOrderRequest("ORD-404"),
                CancellationToken.None));

        Assert.Equal("order_not_found", exception.Fault.Code);
    }
}
