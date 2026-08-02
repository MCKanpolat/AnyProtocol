using System.Diagnostics;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the tracing pipeline stage.
/// </summary>
public sealed class TracingFilter : IMessageFilter
{
    /// <summary>
    /// Gets the activity source.
    /// </summary>
    /// <value>The activity source.</value>
    public static ActivitySource ActivitySource => AnyProtocolDiagnostics.ActivitySource;

    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.Direction == MessageDirection.Inbound &&
            context.Method?.Operation == ContractOperation.Stream)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        using var activity = StartActivity(context);
        try
        {
            await next(context).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            SetError(activity, exception, context.CancellationToken);
            throw;
        }
    }

    internal static Activity? StartActivity(IMessageContext context)
    {
        var kind = context.Direction == MessageDirection.Outbound
            ? ActivityKind.Producer
            : ActivityKind.Consumer;
        var parent = Activity.Current?.Context ?? default;

        if (context.Direction == MessageDirection.Inbound &&
            context.Headers.TryGetValue(HeaderNames.TraceParent, out var traceParent))
        {
            ActivityContext.TryParse(
                traceParent,
                context.Headers[HeaderNames.TraceState],
                isRemote: true,
                out parent);
        }

        var operation = AnyProtocolDiagnostics.GetOperation(context);
        var activity = ActivitySource.StartActivity(
            $"anyprotocol {operation}",
            kind,
            parent);

        if (activity is not null)
        {
            var tags = DiagnosticTags.Create(
                context,
                DiagnosticContext.GetTransportName(context),
                operation);
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }

            if (context.Direction == MessageDirection.Outbound)
            {
                context.Headers.Set(HeaderNames.TraceParent, activity.Id!);
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    context.Headers.Set(HeaderNames.TraceState, activity.TraceStateString);
                }
            }
        }

        return activity;
    }

    internal static void SetError(
        Activity? activity,
        Exception exception,
        CancellationToken cancellationToken)
    {
        activity?.SetTag("exception.type", exception.GetType().FullName);
        activity?.SetTag(
            "anyprotocol.outcome",
            AnyProtocolDiagnostics.GetOutcome(exception, cancellationToken));
        activity?.SetStatus(ActivityStatusCode.Error);
    }
}
