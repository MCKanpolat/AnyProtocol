using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Converts message deadlines into linked cancellation sources.
/// </summary>
internal sealed class DeadlineCancellationFactory
{
    private readonly IMessageEnvelopeFactory _envelopeFactory;

    public DeadlineCancellationFactory(IMessageEnvelopeFactory envelopeFactory)
    {
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
    }

    public CancellationTokenSource? Create(
        TransportEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(
                request.Headers[HeaderNames.Deadline],
                out var deadline))
        {
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - _envelopeFactory.GetUtcNow();
        source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return source;
    }
}