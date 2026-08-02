using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the pipeline builder implementation used by AnyProtocol applications.
/// </summary>
public static class PipelineBuilder
{
    /// <summary>
    /// Performs the build operation.
    /// </summary>
    /// <param name="filters">The filters.</param>
    /// <param name="terminal">The terminal.</param>
    /// <returns>The result of the build operation.</returns>
    public static MessageFilterDelegate Build(
        IEnumerable<IMessageFilter> filters,
        MessageFilterDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(terminal);

        var pipeline = terminal;
        foreach (var filter in filters.Reverse())
        {
            var next = pipeline;
            pipeline = context => filter.InvokeAsync(context, next);
        }

        return pipeline;
    }
}
