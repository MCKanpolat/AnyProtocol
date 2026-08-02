using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using OrderSystem.Contracts;

namespace OrderSystem.Client.Services;

public static class OrderScenario
{
    public static async Task RunAsync(
        IOrderService service,
        ProtocolKey protocol,
        bool showError,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(output);

        var protocolName = protocol == ProtocolKey.Rest ? "Rest" : "Grpc";
        output.WriteLine($"Protocol: {protocolName}");

        var created = await service.CreateAsync(
            new CreateOrderRequest("customer-42", 125.50m),
            cancellationToken);
        output.WriteLine($"Created order: {created.OrderId} ({created.Status})");

        var fetched = await service.GetAsync(new GetOrderRequest(created.OrderId), cancellationToken);
        output.WriteLine($"Retrieved order: {fetched.OrderId} ({fetched.Status})");

        var listed = await service.ListAsync(new ListOrdersRequest(), cancellationToken);
        output.WriteLine($"Listed orders: {listed.Orders.Count}");

        if (!showError)
        {
            return;
        }

        try
        {
            await service.GetAsync(new GetOrderRequest("ORD-404"), cancellationToken);
        }
        catch (AnyProtocolFaultException exception) when (
            string.Equals(exception.Fault.Code, "order_not_found", StringComparison.Ordinal))
        {
            output.WriteLine($"Expected fault: {exception.Fault.Code} - {exception.Fault.Message}");
        }
    }
}
