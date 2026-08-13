using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public void Diagnostics_names_are_stable_and_versioned()
    {
        Assert.Equal("AnyProtocol.Core", AnyProtocolDiagnostics.ActivitySourceName);
        Assert.Equal("AnyProtocol.Core", AnyProtocolDiagnostics.MeterName);
        Assert.Equal("1.0.0", AnyProtocolDiagnostics.Version);
        Assert.Equal(
            "anyprotocol.messaging.operations.completed",
            AnyProtocolDiagnostics.OperationsCompletedName);
        Assert.Equal(
            "anyprotocol.messaging.handler.duration",
            AnyProtocolDiagnostics.HandlerDurationName);
    }

    [Fact]
    public async Task Metrics_account_for_success_and_fault_with_bounded_tags()
    {
        using var metrics = new MetricCollector();
        var transport = new InMemoryMessagingProtocol();
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", transport)
            .AddClient<IOrderService>(client => client.UseTransport("memory"))
            .AddServer<IOrderService, OrderService>(server => server.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();
        var client = provider.GetRequiredService<IOrderService>();

        _ = await client.PlaceAsync(new PlaceOrderRequest("metric-secret"), CancellationToken.None);
        _ = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await client.FailAsync(new PlaceOrderRequest("payload-secret")));

        var completed = metrics.Measurements
            .Where(item => item.Instrument == AnyProtocolDiagnostics.OperationsCompletedName)
            .Where(item => item.Tags.GetValueOrDefault("anyprotocol.contract") ==
                           typeof(IOrderService).FullName)
            .ToArray();
        Assert.Contains(
            completed,
            item => item.Tags.GetValueOrDefault("anyprotocol.outcome") == "success");
        Assert.Contains(
            completed,
            item => item.Tags.GetValueOrDefault("anyprotocol.outcome") == "fault");
        Assert.All(
            completed,
            item =>
            {
                Assert.Subset(
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        "anyprotocol.contract",
                        "anyprotocol.method",
                        "anyprotocol.transport",
                        "anyprotocol.operation",
                        "anyprotocol.outcome"
                    },
                    item.Tags.Keys.ToHashSet(StringComparer.Ordinal));
                var serialized = string.Join("|", item.Tags.Select(static pair => pair.Value));
                Assert.DoesNotContain("metric-secret", serialized, StringComparison.Ordinal);
                Assert.DoesNotContain("payload-secret", serialized, StringComparison.Ordinal);
                Assert.DoesNotContain("cl-message-id", serialized, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task Metrics_account_for_timeout_and_retry_outcomes()
    {
        using var metrics = new MetricCollector();
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("timeout", new TimeoutTransport())
            .AddClient<IOrderService>(client => client.UseTransport("timeout")));
        await using var provider = services.BuildServiceProvider();

        _ = await Assert.ThrowsAsync<TimeoutException>(
            async () => await provider.GetRequiredService<IOrderService>()
                .PlaceAsync(new PlaceOrderRequest("timeout"), CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var cancelledServices = new ServiceCollection();
        cancelledServices.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("cancel", new CancellationTransport(cancellation))
            .AddClient<IOrderService>(client => client.UseTransport("cancel")));
        await using var cancelledProvider = cancelledServices.BuildServiceProvider();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelledProvider.GetRequiredService<IOrderService>()
                .PlaceAsync(new PlaceOrderRequest("cancel"), cancellation.Token));

        var method = new ContractDescriptorFactory()
            .Create<IRetryDiagnosticService>()
            .Methods
            .Single();
        var context = new MessageContext(
            new MessageHeaders
            {
                [HeaderNames.Contract] = method.ContractName,
                [HeaderNames.Method] = method.MethodName
            },
            ReadOnlyMemory<byte>.Empty,
            method.Channel,
            MessageType.Request,
            MessageDirection.Outbound)
        {
            Method = method
        };
        var attempts = 0;
        await new RetryFilter(
                new RetryOptions
                {
                    MaxAttempts = 2,
                    InitialDelay = TimeSpan.Zero,
                    MaxDelay = TimeSpan.Zero,
                    JitterFactor = 0
                })
            .InvokeAsync(
                context,
                _ =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        throw new IOException("transient");
                    }

                    return ValueTask.CompletedTask;
                });

        Assert.Contains(
            metrics.Measurements,
            item => item.Instrument == AnyProtocolDiagnostics.OperationsCompletedName &&
                    item.Tags.GetValueOrDefault("anyprotocol.outcome") == "timeout");
        Assert.Contains(
            metrics.Measurements,
            item => item.Instrument == AnyProtocolDiagnostics.OperationsCompletedName &&
                    item.Tags.GetValueOrDefault("anyprotocol.outcome") == "cancelled");
        Assert.Contains(
            metrics.Measurements,
            item => item.Instrument == AnyProtocolDiagnostics.RetriesName);
    }

    [Fact]
    public async Task Trace_context_propagates_from_client_to_handler()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AnyProtocolDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        var transport = new InMemoryMessagingProtocol();
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", transport)
            .AddClient<IOrderService>(client => client.UseTransport("memory"))
            .AddServer<IOrderService, OrderService>(server => server.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();

        _ = await provider.GetRequiredService<IOrderService>()
            .PlaceAsync(new PlaceOrderRequest("trace"), CancellationToken.None);

        var producer = activities.Single(
            activity => activity.Kind == ActivityKind.Producer &&
                        activity.GetTagItem("anyprotocol.method")?.ToString() ==
                        nameof(IOrderService.PlaceAsync));
        var consumer = activities.Single(
            activity => activity.Kind == ActivityKind.Consumer &&
                        activity.GetTagItem("anyprotocol.method")?.ToString() ==
                        nameof(IOrderService.PlaceAsync));
        Assert.Equal(producer.TraceId, consumer.TraceId);
        Assert.Equal(producer.SpanId, consumer.ParentSpanId);
        Assert.DoesNotContain(
            producer.TagObjects,
            static tag => tag.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase) ||
                          tag.Key.Contains("message-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Health_checks_separate_lifecycle_from_readiness()
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", new InMemoryMessagingProtocol()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHealthChecks().AddAnyProtocolHealthChecks();
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();
        var bus = provider.GetRequiredService<IAnyProtocolBus>();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(health, "live")).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(health, "ready")).Status);

        await bus.StartAsync();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(health, "ready")).Status);
    }

    [Fact]
    public async Task Transport_failure_affects_readiness_but_not_liveness()
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("external", new NotReadyTransport()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHealthChecks().AddAnyProtocolHealthChecks();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();
        var health = provider.GetRequiredService<HealthCheckService>();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(health, "live")).Status);
        var readiness = await CheckAsync(health, "ready");
        Assert.Equal(HealthStatus.Unhealthy, readiness.Status);
        Assert.Contains("external", readiness.Entries["anyprotocol_readiness"].Description);
    }

    [Fact]
    public async Task Logging_and_redaction_do_not_expose_secrets_or_payloads()
    {
        const string secret = "super-secret-token";
        Assert.Equal(
            AnyProtocolLogRedactor.Redacted,
            AnyProtocolLogRedactor.RedactValue(HeaderNames.AuthToken, secret));
        Assert.DoesNotContain(
            secret,
            AnyProtocolLogRedactor.RedactConnectionString(
                $"Server=localhost;User Id=app;Password={secret}"),
            StringComparison.Ordinal);
        Assert.Equal(
            AnyProtocolLogRedactor.PayloadOmitted,
            AnyProtocolLogRedactor.Payload(
                includePayload: false,
                Encoding.UTF8.GetBytes("payload-secret")));

        var writer = new CaptureLogWriter();
        var filter = new LoggingFilter(new CaptureLogWriterFactory(writer));
        var headers = new MessageHeaders
        {
            [HeaderNames.AuthToken] = secret,
            [HeaderNames.Contract] = "Safe.Contract",
            [HeaderNames.Method] = "Execute"
        };
        var context = new MessageContext(
            headers,
            Encoding.UTF8.GetBytes("payload-secret"),
            "user-supplied-secret-channel",
            MessageType.Request,
            MessageDirection.Inbound);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await filter.InvokeAsync(
                context,
                _ => throw new InvalidOperationException(secret)));

        var output = string.Join(
            "|",
            writer.Entries.SelectMany(
                static entry => entry.Args.Prepend(entry.Message ?? string.Empty)));
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("user-supplied-secret-channel", output, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), output, StringComparison.Ordinal);
        Assert.Contains(
            writer.Entries,
            entry => entry.EventId == AnyProtocolLogEvents.OperationFaulted);
    }

    private static Task<HealthReport> CheckAsync(HealthCheckService health, string tag)
        => health.CheckHealthAsync(
            registration => registration.Tags.Contains(tag, StringComparer.Ordinal));

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AnyProtocolDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    Add(instrument, measurement, tags));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    Add(instrument, measurement, tags));
            _listener.Start();
        }

        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        public void Dispose() => _listener.Dispose();

        private void Add<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var captured = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                captured[tag.Key] = tag.Value?.ToString();
            }

            Measurements.Enqueue(new Measurement(instrument.Name, value?.ToString(), captured));
        }
    }

    private sealed record Measurement(
        string Instrument,
        string? Value,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed class NotReadyTransport : IMessagingProtocol, ITransportReadiness
    {
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public ValueTask<TransportReadinessResult> CheckReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                TransportReadinessResult.NotReady("The external dependency is unavailable."));

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimeoutTransport : IRequestReplyTransport
    {
        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask<TransportEnvelope> RequestAsync(
            string channel,
            TransportEnvelope request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<TransportEnvelope>(
                new TimeoutException("The deterministic test transport timed out."));

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new TimeoutException());

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellationTransport(CancellationTokenSource cancellation) :
        IRequestReplyTransport
    {
        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask<TransportEnvelope> RequestAsync(
            string channel,
            TransportEnvelope request,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromException<TransportEnvelope>(
                new OperationCanceledException(cancellationToken));
        }

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CaptureLogWriterFactory(CaptureLogWriter writer) : ILogWriterFactory
    {
        public ILogWriter CreateLogWriter(string categoryName) => writer;

        public ILogWriter<T> CreateLogWriter<T>() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogWriter : ILogWriter
    {
        public List<LogEntry> Entries { get; } = [];

        public void Log(
            LogSeverity severity,
            string? message,
            Exception? exception,
            params object?[] args)
            => Entries.Add(new LogEntry(0, message, args));

        public void LogEvent(
            int eventId,
            LogSeverity severity,
            string? message,
            Exception? exception,
            params object?[] args)
            => Entries.Add(new LogEntry(eventId, message, args));

        public bool IsEnabled(LogSeverity severity) => true;
    }

    private sealed record LogEntry(int EventId, string? Message, object?[] Args);
}

public interface IRetryDiagnosticService
{
    [Idempotent]
    ValueTask<PlaceOrderResponse> ExecuteAsync(PlaceOrderRequest request);
}
