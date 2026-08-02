using BenchmarkDotNet.Attributes;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.InMemory;

namespace AnyProtocol.Benchmarks;

[MemoryDiagnoser]
public class InMemoryBenchmarks
{
    private InMemoryMessagingProtocol _transport = null!;
    private IAsyncDisposable _eventSubscription = null!;
    private IAsyncDisposable _requestSubscription = null!;
    private RequestReplyEngine _requestReply = null!;
    private TransportEnvelope _envelope = null!;

    [Params(256, 4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _transport = new InMemoryMessagingProtocol();
        _envelope = new TransportEnvelope(new MessageHeaders(), BenchmarkPayload.Create(PayloadBytes).Data);
        _eventSubscription = await _transport.SubscribeAsync(
            "benchmarks.event",
            static (_, _) => ValueTask.CompletedTask);
        _requestSubscription = await _transport.SubscribeAsync(
            "benchmarks.request",
            async (request, token) =>
            {
                var headers = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await _transport.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(headers, request.Body),
                    token);
            });
        _requestReply = new RequestReplyEngine(_transport);
    }

    [Benchmark(Baseline = true)]
    public ValueTask Event() => _transport.SendAsync("benchmarks.event", _envelope);

    [Benchmark]
    public ValueTask<TransportEnvelope> RequestReply()
        => _requestReply.RequestAsync("benchmarks.request", _envelope, TimeSpan.FromSeconds(5));

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _requestReply.DisposeAsync();
        await _requestSubscription.DisposeAsync();
        await _eventSubscription.DisposeAsync();
        await _transport.DisposeAsync();
    }
}
