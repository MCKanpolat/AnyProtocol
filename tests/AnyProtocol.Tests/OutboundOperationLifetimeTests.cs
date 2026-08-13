using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Services;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class OutboundOperationLifetimeTests
{
    [Fact]
    public async Task Forced_run_cancellation_releases_admission_and_allows_restart()
    {
        var admission = new RequestAdmissionCoordinator();
        using var lifetime = new OutboundOperationLifetime();
        var executor = new OutboundOperationExecutor(
            new EmptyResolverFactory(),
            admission,
            lifetime: lifetime);
        var context = new MessageContext(
            new MessageHeaders(),
            ReadOnlyMemory<byte>.Empty,
            "outbound.lifecycle",
            MessageType.Request,
            MessageDirection.Outbound);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = executor.ExecuteAsync(
            context,
            async current =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, current.CancellationToken);
            }).AsTask();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        admission.BeginDrain();
        lifetime.CancelRun();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(await admission.WaitForIdleAsync(TimeSpan.FromSeconds(1)));

        lifetime.StartRun();
        admission.StartAccepting();
        await executor.ExecuteAsync(
            context,
            current =>
            {
                Assert.False(current.CancellationToken.IsCancellationRequested);
                return ValueTask.CompletedTask;
            });
    }

    private sealed class EmptyResolverFactory : IDependencyResolverFactory
    {
        public IDependencyResolver CreateResolver() => EmptyResolver.Instance;

        public IAsyncDependencyScope CreateAsyncScope() => new EmptyScope();
    }

    private sealed class EmptyScope : IAsyncDependencyScope
    {
        public IDependencyResolver Resolver => EmptyResolver.Instance;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyResolver : IDependencyResolver
    {
        public static readonly EmptyResolver Instance = new();

        public TService? Resolve<TService>() where TService : class => null;

        public object? Resolve(Type serviceType) => null;

        public IEnumerable<TService> ResolveAll<TService>() where TService : class => [];

        public IEnumerable<object?> ResolveAll(Type serviceType) => [];
    }
}
