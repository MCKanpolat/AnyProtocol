using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Configures retry behavior.
/// </summary>
public sealed record RetryOptions
{
    /// <summary>
    /// Gets or initializes the max attempts.
    /// </summary>
    /// <value>The max attempts.</value>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>
    /// Gets or initializes the initial delay.
    /// </summary>
    /// <value>The initial delay.</value>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or initializes the max delay.
    /// </summary>
    /// <value>The max delay.</value>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or initializes the jitter factor.
    /// </summary>
    /// <value>The jitter factor.</value>
    public double JitterFactor { get; init; } = 0.2;

    /// <summary>
    /// Gets or initializes the max total time.
    /// </summary>
    /// <value>The max total time.</value>
    public TimeSpan? MaxTotalTime { get; init; }
}

/// <summary>
/// Processes messages in the retry pipeline stage.
/// </summary>
public sealed class RetryFilter : IMessageFilter
{
    private readonly RetryOptions _options;

    /// <summary>
    /// Initializes a new instance of the RetryFilter class.
    /// </summary>
    /// <param name="maxAttempts">The max attempts.</param>
    public RetryFilter(int maxAttempts)
        : this(new RetryOptions { MaxAttempts = maxAttempts })
    {
    }

    /// <summary>
    /// Initializes a new instance of the RetryFilter class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public RetryFilter(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxAttempts,
                "MaxAttempts must be greater than zero.");
        }

        if (options.InitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "InitialDelay must not be negative.");
        }

        if (options.MaxDelay < options.InitialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxDelay must be greater than or equal to InitialDelay.");
        }

        if (options.JitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "JitterFactor must be between zero and one.");
        }

        if (options.MaxTotalTime is { } maxTotalTime && maxTotalTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxTotalTime must be positive when specified.");
        }

        _options = options;
    }

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
        if (context.Method?.IsIdempotent != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var originalCancellation = context.CancellationToken;
        using var totalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(originalCancellation);
        if (_options.MaxTotalTime is { } maxTotalTime)
        {
            totalCancellation.CancelAfter(maxTotalTime);
        }

        context.CancellationToken = totalCancellation.Token;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                totalCancellation.Token.ThrowIfCancellationRequested();
                context.Response = null;
                context.Exception = null;
                try
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (
                    attempt < _options.MaxAttempts &&
                    IsRetryable(exception))
                {
                    AnyProtocolDiagnostics.RecordRetry(context);
                    var delay = GetDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, totalCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (!originalCancellation.IsCancellationRequested &&
                                  totalCancellation.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"Retry operation exceeded the total time limit " +
                                $"of {_options.MaxTotalTime}.",
                                exception);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (!originalCancellation.IsCancellationRequested &&
                  totalCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Retry operation exceeded the total time limit of {_options.MaxTotalTime}.");
        }
        finally
        {
            context.CancellationToken = originalCancellation;
        }
    }

    private TimeSpan GetDelay(int failedAttempt)
    {
        var exponentialMilliseconds = _options.InitialDelay.TotalMilliseconds *
                                      Math.Pow(2, failedAttempt - 1);
        var cappedMilliseconds = Math.Min(
            exponentialMilliseconds,
            _options.MaxDelay.TotalMilliseconds);
        var jitterMultiplier = 1 +
                               (Random.Shared.NextDouble() * 2 - 1) *
                               _options.JitterFactor;
        return TimeSpan.FromMilliseconds(Math.Max(0, cappedMilliseconds * jitterMultiplier));
    }

    private static bool IsRetryable(Exception exception)
        => exception is TimeoutException or IOException ||
           exception is AnyProtocolFaultException { Fault.Retryable: true };
}
