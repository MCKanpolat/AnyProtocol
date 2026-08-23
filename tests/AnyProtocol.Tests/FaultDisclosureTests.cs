using AnyProtocol.Abstraction;
using AnyProtocol.Storage.Abstraction;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class FaultDisclosureTests
{
    [Fact]
    public void Handler_failed_fault_cannot_override_the_safe_default()
    {
        var fault = FaultMessageSender.CreateFault(
            new AnyProtocolFaultException(
                new FaultMessage(
                    "handler_failed",
                    "password=secret; Server=/private/path",
                    "System.SecretException",
                    Details: [new FaultDetail("connection", "secret")])));

        Assert.Equal("handler_failed", fault.Code);
        Assert.Equal("An unexpected error occurred while processing the request.", fault.Message);
        Assert.Null(fault.ExceptionType);
        Assert.Null(fault.Details);
    }

    [Fact]
    public void Explicit_domain_fault_preserves_approved_fields_without_runtime_type()
    {
        var fault = FaultMessageSender.CreateFault(
            new AnyProtocolFaultException(
                new FaultMessage(
                    "order_rejected",
                    "The order is not eligible.",
                    "System.InvalidOperationException",
                    Details: [new FaultDetail("reason", "closed")])));

        Assert.Equal("order_rejected", fault.Code);
        Assert.Equal("The order is not eligible.", fault.Message);
        Assert.Null(fault.ExceptionType);
        Assert.Equal([new FaultDetail("reason", "closed")], fault.Details);
    }

    [Fact]
    public void Large_payload_fault_does_not_expose_the_runtime_exception_type()
    {
        var fault = FaultMessageSender.CreateFault(
            new LargePayloadException(
                LargePayloadFailureCodes.TooLarge,
                "The configured payload bound was exceeded.",
                retryable: true));

        Assert.Equal(LargePayloadFailureCodes.TooLarge, fault.Code);
        Assert.Equal("The configured payload bound was exceeded.", fault.Message);
        Assert.True(fault.Retryable);
        Assert.Null(fault.ExceptionType);
        Assert.Null(fault.Details);
    }
}
