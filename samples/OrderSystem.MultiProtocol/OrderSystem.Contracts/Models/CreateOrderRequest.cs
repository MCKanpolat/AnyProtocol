namespace OrderSystem.Contracts;

public sealed record CreateOrderRequest(string CustomerId, decimal Total);
