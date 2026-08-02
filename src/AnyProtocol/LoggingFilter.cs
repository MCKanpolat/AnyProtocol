using AnyProtocol.Abstraction;
using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the logging pipeline stage.
/// </summary>
public sealed class LoggingFilter : IMessageFilter
{
    private readonly ILogWriter _logger;

    /// <summary>
    /// Initializes a new instance of the LoggingFilter class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public LoggingFilter(ILogWriterFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogWriter("AnyProtocol.Pipeline");
    }

    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        var contract = context.Method?.ContractName ??
                       context.Headers[HeaderNames.Contract] ??
                       "unknown";
        var method = context.Method?.MethodName ??
                     context.Headers[HeaderNames.Method] ??
                     "unknown";
        _logger.LogEvent(
            AnyProtocolLogEvents.OperationStarted,
            LogSeverity.Debug,
            "Processing {0} operation {1}.{2}.",
            null,
            context.MessageType,
            contract,
            method);

        try
        {
            await next(context).ConfigureAwait(false);
            _logger.LogEvent(
                AnyProtocolLogEvents.OperationCompleted,
                LogSeverity.Debug,
                "Completed {0} operation {1}.{2}.",
                null,
                context.MessageType,
                contract,
                method);
        }
        catch (Exception exception)
        {
            _logger.LogEvent(
                exception is TimeoutException
                    ? AnyProtocolLogEvents.OperationTimedOut
                    : exception is OperationCanceledException
                        ? AnyProtocolLogEvents.OperationCancelled
                        : AnyProtocolLogEvents.OperationFaulted,
                LogSeverity.Error,
                "Failed {0} operation {1}.{2}; exception type {3}.",
                null,
                context.MessageType,
                contract,
                method,
                exception.GetType().FullName ?? exception.GetType().Name);
            throw;
        }
    }
}
