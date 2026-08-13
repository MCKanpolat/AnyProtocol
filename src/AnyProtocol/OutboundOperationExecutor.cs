using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Owns outbound operation admission, dependency scopes, and configured filter execution.
/// </summary>
public sealed class OutboundOperationExecutor
{
    private readonly IDependencyResolverFactory _resolverFactory;
    private readonly IRequestAdmission _admission;
    private readonly OutboundOperationLifetime _lifetime;
    private readonly IReadOnlyList<IMessageFilter> _filters;

    /// <summary>Initializes an outbound operation executor.</summary>
    public OutboundOperationExecutor(
        IDependencyResolverFactory resolverFactory,
        IRequestAdmission admission,
        IEnumerable<IMessageFilter>? filters = null,
        OutboundOperationLifetime? lifetime = null)
    {
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _lifetime = lifetime ?? new OutboundOperationLifetime();
        _filters = (filters ?? []).ToArray();
    }

    /// <summary>Builds a reusable outbound filter pipeline around a terminal operation.</summary>
    public MessageFilterDelegate CreatePipeline(
        MessageFilterDelegate terminal,
        bool includeObservability = true)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var filters = includeObservability
            ? ObservabilityFilters.AddDefaults(_filters)
            : _filters;
        return PipelineBuilder.Build(filters, terminal);
    }

    /// <summary>Executes one finite outbound operation.</summary>
    public async ValueTask ExecuteAsync(IMessageContext context, MessageFilterDelegate pipeline)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        using var admission = _admission.TryEnter() ?? throw new InvalidOperationException(
            "AnyProtocol is draining and cannot accept a new outbound operation.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            _lifetime.Token);
        await using var scope = _resolverFactory.CreateAsyncScope();
        var scopedContext = MessageContextRuntime.WithServices(
            MessageContextRuntime.WithCancellation(context, cancellation.Token),
            scope.Resolver);
        await pipeline(scopedContext).ConfigureAwait(false);
    }

    /// <summary>Executes an outbound stream while retaining its admission lease and scope.</summary>
    public async IAsyncEnumerable<T> ExecuteStreamAsync<T>(
        IMessageContext context,
        MessageFilterDelegate pipeline,
        Func<IMessageContext, IAsyncEnumerable<T>> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(operation);
        using var admission = _admission.TryEnter() ?? throw new InvalidOperationException(
            "AnyProtocol is draining and cannot accept a new outbound operation.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.CancellationToken,
            _lifetime.Token);
        await using var scope = _resolverFactory.CreateAsyncScope();
        var scopedContext = MessageContextRuntime.WithServices(
            MessageContextRuntime.WithCancellation(context, cancellation.Token),
            scope.Resolver);
        await pipeline(scopedContext).ConfigureAwait(false);
        await foreach (var item in operation(scopedContext)
                           .WithCancellation(cancellation.Token)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
