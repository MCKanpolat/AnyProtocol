using AnyProtocol.Configuration;
using AnyProtocol.Storage.Abstraction;
using Microsoft.Extensions.Hosting;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class InboxConfigurationValidationService(
    RuntimePlan runtimePlan,
    IEnumerable<IMessageDeduplicationStore> stores) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var available = stores.Select(static store => store.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = runtimePlan.RegisteredServerFilters
            .OfType<InboxDeduplicationFilter>()
            .Select(static filter => filter.StoreName)
            .Where(storeName => !available.Contains(storeName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Inbox deduplication requires missing IMessageDeduplicationStore " +
                $"registrations: {string.Join(", ", missing.Select(static name => $"'{name}'"))}.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
