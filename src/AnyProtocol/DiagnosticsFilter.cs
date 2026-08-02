using AnyProtocol.Abstraction;

namespace AnyProtocol;

internal sealed class DiagnosticsFilter : IMessageFilter
{
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        if (context.Direction == MessageDirection.Inbound &&
            context.Method?.Operation == ContractOperation.Stream)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var measurement = AnyProtocolDiagnostics.StartOperation(
            context,
            DiagnosticContext.GetTransportName(context));
        try
        {
            await next(context).ConfigureAwait(false);
            var outcome = context.Response?.Headers.Get(
                HeaderNames.MessageType,
                MessageType.Response) == MessageType.Fault
                ? "fault"
                : "success";
            measurement.Complete(outcome);
        }
        catch (Exception exception)
        {
            measurement.Complete(
                AnyProtocolDiagnostics.GetOutcome(exception, context.CancellationToken));
            throw;
        }
    }
}
