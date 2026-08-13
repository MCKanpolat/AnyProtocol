using AnyProtocol.Abstraction;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>Configures inbox lease and completed-record lifetimes.</summary>
public sealed record InboxDeduplicationOptions
{
    /// <summary>Gets or initializes the logical deduplication store name.</summary>
    public required string StoreName { get; init; }

    /// <summary>Gets or initializes how long an abandoned worker may own an in-flight lease.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or initializes how long a completed message remains deduplicated.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>Prevents repeated execution of one-way messages with the same message ID.</summary>
public sealed class InboxDeduplicationFilter : IMessageFilter
{
    /// <summary>The context item set to <see langword="true"/> for a suppressed duplicate.</summary>
    public const string DuplicateItemKey = "anyprotocol.inbox.duplicate";

    private readonly InboxDeduplicationOptions _options;

    /// <summary>Creates an inbox filter for one named store.</summary>
    public InboxDeduplicationFilter(InboxDeduplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StoreName);
        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "LeaseDuration must be positive.");
        }

        if (options.Retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Retention must be positive.");
        }

        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!RequiresInbox(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var messageId = context.Headers[HeaderNames.MessageId];
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new InvalidOperationException(
                $"Inbound one-way message on channel '{context.Channel}' has no " +
                $"'{HeaderNames.MessageId}' header and cannot be deduplicated.");
        }

        var store = ResolveStore(context);
        var lease = await store.TryAcquireAsync(
                messageId,
                _options.LeaseDuration,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            context.Items[DuplicateItemKey] = true;
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
            await store.CompleteAsync(lease.Value, _options.Retention, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await store.ReleaseAsync(lease.Value, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal string StoreName => _options.StoreName;

    private static bool RequiresInbox(IMessageContext context)
        => context.Direction == MessageDirection.Inbound &&
           (context.MessageType == MessageType.Event ||
            context.Method?.Operation == ContractOperation.Send);

    private IMessageDeduplicationStore ResolveStore(IMessageContext context)
    {
        var stores = MessageContextRuntime.Get(context).Services?
            .ResolveAll<IMessageDeduplicationStore>() ?? [];
        return stores.SingleOrDefault(
                   store => string.Equals(store.Name, _options.StoreName, StringComparison.OrdinalIgnoreCase)) ??
               throw new InvalidOperationException(
                   $"No message deduplication store named '{_options.StoreName}' is registered.");
    }
}
