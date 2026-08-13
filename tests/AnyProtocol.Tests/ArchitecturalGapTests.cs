using AnyProtocol.Abstraction;
using AnyProtocol.Authorization.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Storage.Abstraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class ArchitecturalGapTests
{
    [Fact]
    public async Task Protected_routes_fail_startup_without_authorization_filter()
    {
        await using var provider = CreateProtectedProvider(addFilter: false, addProvider: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetAuthorizationValidator(provider).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(AuthorizationFilter), exception.Message);
        Assert.Contains(nameof(IProtectedService.ReadAsync), exception.Message);
    }

    [Fact]
    public async Task Protected_routes_fail_startup_without_authorization_provider()
    {
        await using var provider = CreateProtectedProvider(addFilter: true, addProvider: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetAuthorizationValidator(provider).StartAsync(CancellationToken.None));

        Assert.Contains(nameof(IAuthorizationProvider), exception.Message);
        Assert.Contains(nameof(IProtectedService.ReadAsync), exception.Message);
    }

    [Fact]
    public async Task Protected_routes_accept_complete_authorization_configuration()
    {
        await using var provider = CreateProtectedProvider(addFilter: true, addProvider: true);

        await GetAuthorizationValidator(provider).StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Inbox_filter_fails_startup_when_named_store_is_missing()
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", new InMemoryMessagingProtocol())
            .AddServerFilter(new InboxDeduplicationFilter(
                new InboxDeduplicationOptions { StoreName = "missing" })));
        await using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>().Single(
            static service => service.GetType().Name == "InboxConfigurationValidationService");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("missing", exception.Message);
        Assert.Contains(nameof(IMessageDeduplicationStore), exception.Message);
    }

    [Fact]
    public async Task Inbox_filter_suppresses_a_completed_duplicate()
    {
        var store = new InMemoryMessageDeduplicationStore();
        var filter = CreateInboxFilter();
        var resolver = new TestResolver(store);
        var invocations = 0;

        var first = CreateEventContext("message-1", resolver);
        await filter.InvokeAsync(first, _ =>
        {
            invocations++;
            return ValueTask.CompletedTask;
        });

        var duplicate = CreateEventContext("message-1", resolver);
        await filter.InvokeAsync(duplicate, _ =>
        {
            invocations++;
            return ValueTask.CompletedTask;
        });

        Assert.Equal(1, invocations);
        Assert.Equal(true, duplicate.Items[InboxDeduplicationFilter.DuplicateItemKey]);
    }

    [Fact]
    public async Task Inbox_filter_releases_failed_work_for_redelivery()
    {
        var store = new InMemoryMessageDeduplicationStore();
        var filter = CreateInboxFilter();
        var resolver = new TestResolver(store);
        var invocations = 0;

        await Assert.ThrowsAsync<IOException>(
            () => filter.InvokeAsync(
                    CreateEventContext("message-1", resolver),
                    _ =>
                    {
                        invocations++;
                        return ValueTask.FromException(new IOException("failed"));
                    })
                .AsTask());
        await filter.InvokeAsync(
            CreateEventContext("message-1", resolver),
            _ =>
            {
                invocations++;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task Inbox_store_allows_only_one_concurrent_owner()
    {
        var store = new InMemoryMessageDeduplicationStore();

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(
                    _ => store.TryAcquireAsync("message-1", TimeSpan.FromMinutes(1))
                        .AsTask()));

        Assert.Single(leases, static lease => lease is not null);
    }

    private static ServiceProvider CreateProtectedProvider(bool addFilter, bool addProvider)
    {
        var services = new ServiceCollection();
        if (addProvider)
        {
            services.AddSingleton<IAuthorizationProvider, AllowAuthorizationProvider>();
        }

        services.AddAnyProtocol(
            link =>
            {
                link.UseSerializer(new TextJsonMessageSerializer())
                    .AddTransport("memory", new InMemoryMessagingProtocol())
                    .AddServer<IProtectedService, ProtectedService>(
                        server => server.UseTransport("memory"));
                if (addFilter)
                {
                    link.AddServerFilter(new AuthorizationFilter());
                }
            });
        return services.BuildServiceProvider();
    }

    private static IHostedService GetAuthorizationValidator(IServiceProvider provider)
        => provider.GetServices<IHostedService>().Single(
            static service => service.GetType().Name ==
                              "AuthorizationConfigurationValidationService");

    private static InboxDeduplicationFilter CreateInboxFilter()
        => new(
            new InboxDeduplicationOptions
            {
                StoreName = "in-memory",
                LeaseDuration = TimeSpan.FromMinutes(1),
                Retention = TimeSpan.FromHours(1)
            });

    private static MessageContext CreateEventContext(
        string messageId,
        IDependencyResolver resolver)
    {
        var context = new MessageContext(
            new MessageHeaders { [HeaderNames.MessageId] = messageId },
            ReadOnlyMemory<byte>.Empty,
            "events",
            MessageType.Event,
            MessageDirection.Inbound);
        context.Invocation = new MessageInvocation(CancellationToken.None, resolver);
        return context;
    }

    [RequirePermission("orders.read")]
    public interface IProtectedService
    {
        Task ReadAsync();
    }

    public sealed class ProtectedService : IProtectedService
    {
        public Task ReadAsync() => Task.CompletedTask;
    }

    private sealed class AllowAuthorizationProvider : IAuthorizationProvider
    {
        public ValueTask<bool> CheckPermissionsAsync(
            IEnumerable<string> permissions,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class TestResolver(params object[] services) : IDependencyResolver
    {
        public TService? Resolve<TService>() where TService : class
            => services.OfType<TService>().SingleOrDefault();

        public object? Resolve(Type serviceType)
            => services.SingleOrDefault(serviceType.IsInstanceOfType);

        public IEnumerable<TService> ResolveAll<TService>() where TService : class
            => services.OfType<TService>();

        public IEnumerable<object?> ResolveAll(Type serviceType)
            => services.Where(serviceType.IsInstanceOfType);
    }
}
