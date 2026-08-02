using System.Diagnostics;
using System.Diagnostics.Metrics;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>Stable diagnostics names and instruments emitted by AnyProtocol.</summary>
public static class AnyProtocolDiagnostics
{
    /// <summary>
    /// The version value.
    /// </summary>
    public const string Version = "1.0.0";
    /// <summary>
    /// The activity source name value.
    /// </summary>
    public const string ActivitySourceName = "AnyProtocol.Core";
    /// <summary>
    /// The meter name value.
    /// </summary>
    public const string MeterName = "AnyProtocol.Core";

    /// <summary>
    /// The operations started name value.
    /// </summary>
    public const string OperationsStartedName = "anyprotocol.messaging.operations.started";
    /// <summary>
    /// The operations completed name value.
    /// </summary>
    public const string OperationsCompletedName = "anyprotocol.messaging.operations.completed";
    /// <summary>
    /// The operation duration name value.
    /// </summary>
    public const string OperationDurationName = "anyprotocol.messaging.operation.duration";
    /// <summary>
    /// The handler duration name value.
    /// </summary>
    public const string HandlerDurationName = "anyprotocol.messaging.handler.duration";
    /// <summary>
    /// The active requests name value.
    /// </summary>
    public const string ActiveRequestsName = "anyprotocol.messaging.requests.active";
    /// <summary>
    /// The active streams name value.
    /// </summary>
    public const string ActiveStreamsName = "anyprotocol.messaging.streams.active";
    /// <summary>
    /// The retries name value.
    /// </summary>
    public const string RetriesName = "anyprotocol.messaging.retries";
    /// <summary>
    /// The dead letters name value.
    /// </summary>
    public const string DeadLettersName = "anyprotocol.messaging.dead_letters";

    /// <summary>
    /// Performs the activity source operation.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);
    /// <summary>
    /// Performs the meter operation.
    /// </summary>
    public static readonly Meter Meter = new(MeterName, Version);

    private static readonly Counter<long> OperationsStarted =
        Meter.CreateCounter<long>(OperationsStartedName, "{operation}");
    private static readonly Counter<long> OperationsCompleted =
        Meter.CreateCounter<long>(OperationsCompletedName, "{operation}");
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>(OperationDurationName, "s");
    private static readonly Histogram<double> HandlerDuration =
        Meter.CreateHistogram<double>(HandlerDurationName, "s");
    private static readonly UpDownCounter<long> ActiveRequests =
        Meter.CreateUpDownCounter<long>(ActiveRequestsName, "{request}");
    private static readonly UpDownCounter<long> ActiveStreams =
        Meter.CreateUpDownCounter<long>(ActiveStreamsName, "{stream}");
    private static readonly Counter<long> Retries =
        Meter.CreateCounter<long>(RetriesName, "{retry}");
    private static readonly Counter<long> DeadLetters =
        Meter.CreateCounter<long>(DeadLettersName, "{message}");

    internal static OperationMeasurement StartOperation(
        IMessageContext context,
        string? transportName,
        string? operationOverride = null)
    {
        var operation = operationOverride ?? GetOperation(context);
        var tags = DiagnosticTags.Create(context, transportName, operation);
        OperationsStarted.Add(1, tags);
        AddActive(operation, 1, tags);
        return new OperationMeasurement(operation, tags);
    }

    internal static void RecordHandler(
        IMessageContext context,
        string? transportName,
        long startedAt,
        string outcome)
    {
        var tags = DiagnosticTags.Create(context, transportName, GetOperation(context), outcome);
        HandlerDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
    }

    internal static void RecordRetry(IMessageContext context)
        => Retries.Add(
            1,
            DiagnosticTags.Create(
                context,
                DiagnosticContext.GetTransportName(context),
                GetOperation(context)));

    internal static void RecordDeadLetter(
        IMessageHeaders headers,
        string contract,
        string? method,
        string transportName)
        => DeadLetters.Add(
            1,
            DiagnosticTags.Create(contract, method, transportName, "event", "fault"));

    internal static string GetOutcome(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            TimeoutException => "timeout",
            OperationCanceledException when cancellationToken.IsCancellationRequested => "cancelled",
            OperationCanceledException => "timeout",
            _ => "fault"
        };

    internal static string GetOperation(IMessageContext context)
        => context.Method?.Operation switch
        {
            ContractOperation.Stream => "stream",
            ContractOperation.Send when context.MessageType == MessageType.Event => "event",
            _ when context.MessageType == MessageType.Event => "event",
            _ => "request"
        };

    private static void AddActive(string operation, long delta, in TagList tags)
    {
        if (operation == "stream")
        {
            ActiveStreams.Add(delta, tags);
        }
        else if (operation == "request")
        {
            ActiveRequests.Add(delta, tags);
        }
    }

    internal sealed class OperationMeasurement(string operation, TagList tags) : IDisposable
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _completed;

        public void Complete(string outcome)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            var completedTags = tags;
            completedTags.Add("anyprotocol.outcome", DiagnosticTags.NormalizeOutcome(outcome));
            OperationsCompleted.Add(1, completedTags);
            OperationDuration.Record(
                Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
                completedTags);
            AddActive(operation, -1, tags);
        }

        public void Dispose() => Complete("success");
    }
}

internal static class DiagnosticContext
{
    internal const string TransportNameKey = "anyprotocol.diagnostics.transport";

    internal static string? GetTransportName(IMessageContext context)
        => context.Items.TryGetValue(TransportNameKey, out var value) ? value as string : null;
}

internal static class DiagnosticTags
{
    private const int MaximumIdentifierLength = 128;

    internal static TagList Create(
        IMessageContext context,
        string? transportName,
        string operation,
        string? outcome = null)
        => Create(
            context.Method?.ContractName ?? context.Headers[HeaderNames.Contract],
            context.Method?.MethodName ?? context.Headers[HeaderNames.Method],
            transportName,
            operation,
            outcome);

    internal static TagList Create(
        string? contract,
        string? method,
        string? transportName,
        string operation,
        string? outcome = null)
    {
        var tags = new TagList
        {
            { "anyprotocol.contract", NormalizeIdentifier(contract) },
            { "anyprotocol.method", NormalizeIdentifier(method) },
            { "anyprotocol.transport", NormalizeIdentifier(transportName) },
            { "anyprotocol.operation", NormalizeOperation(operation) }
        };
        if (outcome is not null)
        {
            tags.Add("anyprotocol.outcome", NormalizeOutcome(outcome));
        }

        return tags;
    }

    internal static string NormalizeOutcome(string? value)
        => value is "success" or "fault" or "timeout" or "cancelled"
            ? value
            : "unknown";

    private static string NormalizeOperation(string? value)
        => value is "request" or "event" or "stream" ? value : "unknown";

    private static string NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var length = Math.Min(value.Length, MaximumIdentifierLength);
        return string.Create(
            length,
            value,
            static (span, source) =>
            {
                for (var index = 0; index < span.Length; index++)
                {
                    var character = source[index];
                    span[index] = char.IsAsciiLetterOrDigit(character) ||
                                  character is '.' or '_' or '-' or '+'
                        ? character
                        : '_';
                }
            });
    }
}
