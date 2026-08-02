namespace OrderSystem.Contracts;

public sealed record OrderResponse(string OrderId, string CustomerId, decimal Total, string Status);
