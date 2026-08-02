namespace OrderSystem.Contracts;

public sealed record OrderListResponse(IReadOnlyList<OrderResponse> Orders);
