using System.Collections.Concurrent;
using System.Diagnostics;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.LoadTests;

public sealed class LoadScenarioRunner
{
    public async Task<ScenarioResult> RunAsync(
        LoadTestOptions options,
        ILoadTransportFactory factory,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        await using var transport = await factory.CreateAsync(cancellationToken);
        var sender = transport as ISendTransport ??
            throw new InvalidOperationException("Load transports must implement ISendTransport.");
        var subscriber = transport as ISubscriptionTransport ??
            throw new InvalidOperationException("Load transports must implement ISubscriptionTransport.");
        var pending = new ConcurrentDictionary<string, TaskCompletionSource>();
        var subscriptions = new List<IAsyncDisposable>();
        RequestReplyEngine? requestReply = null;
        try
        {
            var channel = $"performance.{options.Scenario}";
            if (options.Scenario == "request-reply")
            {
                subscriptions.Add(await subscriber.SubscribeAsync(
                    channel,
                    async (request, token) =>
                    {
                        var headers = new MessageHeaders
                        {
                            [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                            [HeaderNames.MessageType] = MessageType.Response.ToString()
                        };
                        await sender.SendAsync(
                            request.Headers[HeaderNames.ReplyTo]!,
                            new TransportEnvelope(headers, request.Body),
                            token);
                    },
                    new SubscriptionOptions { ConsumerGroup = "performance-responders", MaxConcurrency = options.Concurrency },
                    cancellationToken));
                requestReply = new RequestReplyEngine(transport);
            }
            else
            {
                ValueTask Handle(TransportEnvelope envelope, CancellationToken _)
                {
                    var id = envelope.Headers[HeaderNames.MessageId];
                    if (id is not null && pending.TryRemove(id, out var completion))
                    {
                        completion.TrySetResult();
                    }

                    return ValueTask.CompletedTask;
                }

                var subscriptionOptions = options.Scenario == "competing-consumers"
                    ? new SubscriptionOptions { ConsumerGroup = "performance-workers", MaxConcurrency = options.Concurrency }
                    : new SubscriptionOptions { MaxConcurrency = options.Concurrency };
                subscriptions.Add(await subscriber.SubscribeAsync(channel, Handle, subscriptionOptions, cancellationToken));
                if (options.Scenario == "competing-consumers")
                {
                    subscriptions.Add(await subscriber.SubscribeAsync(channel, Handle, subscriptionOptions, cancellationToken));
                }
            }

            var payload = CreatePayload(options.PayloadBytes);
            await ExecuteAsync(channel, payload, sender, transport, requestReply, pending, options.OperationTimeout, cancellationToken);
            for (var index = 0; index < options.WarmupOperations; index++)
            {
                await ExecuteAsync(channel, payload, sender, transport, requestReply, pending, options.OperationTimeout, cancellationToken);
            }

            var recorder = new LatencyRecorder();
            var errors = 0;
            var timeouts = 0;
            var nextOperation = -1;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var stopwatch = Stopwatch.StartNew();
            var workers = Enumerable.Range(0, options.Concurrency).Select(async _ =>
            {
                await start.Task.WaitAsync(cancellationToken);
                while (true)
                {
                    var operation = Interlocked.Increment(ref nextOperation);
                    if (operation >= options.Operations)
                    {
                        return;
                    }

                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        await ExecuteAsync(channel, payload, sender, transport, requestReply, pending, options.OperationTimeout, cancellationToken);
                        recorder.Record(Stopwatch.GetElapsedTime(started));
                    }
                    catch (TimeoutException)
                    {
                        Interlocked.Increment(ref timeouts);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }).ToArray();
            start.TrySetResult();
            await Task.WhenAll(workers);
            stopwatch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var collections = Enumerable.Range(0, 3)
                .Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation])
                .ToArray();
            var completed = options.Operations - errors - timeouts;
            var statistics = completed == 0
                ? new LatencyStatistics(0, 0, 0, 0)
                : recorder.Complete(completed, stopwatch.Elapsed);
            return new ScenarioResult(
                factory.Name,
                options.Scenario,
                options.PayloadBytes,
                options.Concurrency,
                options.Operations,
                statistics.OperationsPerSecond,
                statistics.P50Milliseconds,
                statistics.P95Milliseconds,
                statistics.P99Milliseconds,
                allocated,
                collections[0],
                collections[1],
                collections[2],
                errors,
                timeouts);
        }
        finally
        {
            if (requestReply is not null)
            {
                await requestReply.DisposeAsync();
            }

            foreach (var subscription in subscriptions)
            {
                await subscription.DisposeAsync();
            }
        }
    }

    private static async Task ExecuteAsync(
        string channel,
        byte[] payload,
        ISendTransport sender,
        IMessagingProtocol transport,
        RequestReplyEngine? requestReply,
        ConcurrentDictionary<string, TaskCompletionSource> pending,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (requestReply is not null)
        {
            var response = await requestReply.RequestAsync(
                channel,
                new TransportEnvelope(new MessageHeaders(), payload),
                timeout,
                cancellationToken);
            if (!response.Body.Span.SequenceEqual(payload))
            {
                throw new InvalidDataException("Request/reply payload was corrupted.");
            }

            return;
        }

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(timeout);
        var operationCancellationToken = operationTimeout.Token;
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register load operation.");
        }

        try
        {
            await sender.SendAsync(
                channel,
                new TransportEnvelope(new MessageHeaders { [HeaderNames.MessageId] = id }, payload),
                operationCancellationToken);
            await completion.Task.WaitAsync(operationCancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Load operation on '{channel}' did not complete within {timeout}.");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }
}
