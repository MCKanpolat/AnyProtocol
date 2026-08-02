using AnyProtocol.Abstraction;

namespace AnyProtocol;

internal static class ObservabilityFilters
{
    internal static IEnumerable<IMessageFilter> AddDefaults(IEnumerable<IMessageFilter>? filters)
    {
        var configured = (filters ?? []).ToArray();
        yield return new DiagnosticsFilter();
        if (!configured.Any(static filter => filter is TracingFilter))
        {
            yield return new TracingFilter();
        }

        foreach (var filter in configured)
        {
            yield return filter;
        }
    }
}
