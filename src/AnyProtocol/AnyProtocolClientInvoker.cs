using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the anyprotocol client invoker implementation used by AnyProtocol applications.
/// </summary>
public sealed class AnyProtocolClientInvoker : IClientInvoker, IAsyncDisposable
{
    private const string ContentType = "application/x-anyprotocol";
    private readonly TransportRegistry _transports;
    private readonly IMessageSerializer _serializer;
    private readonly IReadOnlyDictionary<Type, ClientRegistration> _registrations;
    private readonly MessageFilterDelegate _pipeline;
    private readonly IDependencyResolver? _services;
    private readonly ConcurrentDictionary<IMessagingProtocol, RequestReplyEngine> _engines = new();
    private readonly ConcurrentDictionary<IMessagingProtocol, StreamEngine> _streamEngines = new();

    /// <summary>
    /// Initializes a new instance of the AnyProtocolClientInvoker class.
    /// </summary>
    /// <param name="transports">The transports.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="registrations">The registrations.</param>
    /// <param name="filters">The filters.</param>
    /// <param name="services">The service collection to configure.</param>
    public AnyProtocolClientInvoker(
        TransportRegistry transports,
        IMessageSerializer serializer,
        IEnumerable<ClientRegistration> registrations,
        IEnumerable<IMessageFilter>? filters = null,
        IDependencyResolver? services = null)
    {
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registrations = registrations.ToDictionary(registration => registration.ContractType);
        _services = services;
        _pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            InvokeTransportAsync);
    }

    /// <summary>
    /// Performs the request async&lt;t request, t response&gt; operation.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the request async&lt;t request, t response&gt;.</returns>
    public async ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateContextAsync(method, request, cancellationToken).ConfigureAwait(false);
        await _pipeline(context).ConfigureAwait(false);
        var response = context.Response ??
                       throw new InvalidOperationException($"Method '{method.MethodName}' produced no response.");
        ThrowIfFault(response);
        var result = await _serializer.DeserializeAsync<TResponse>(response.Body, cancellationToken)
            .ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Performs the send async&lt;t request&gt; operation.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask SendAsync<TRequest>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await CreateContextAsync(method, request, cancellationToken).ConfigureAwait(false);
        await _pipeline(context).ConfigureAwait(false);
        if (context.Response is not null)
        {
            ThrowIfFault(context.Response);
        }
    }

    /// <summary>
    /// Performs the stream async&lt;t request, t item&gt; operation.
    /// </summary>
    /// <typeparam name="TRequest">The request payload type.</typeparam>
    /// <typeparam name="TItem">The item type.</typeparam>
    /// <param name="method">The method.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    public async IAsyncEnumerable<TItem> StreamAsync<TRequest, TItem>(
        ContractMethodDescriptor method,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var registration = GetRegistration(method.ContractType);
        var transport = _transports.GetRequired(registration.TransportName);
        var envelope = await CreateEnvelopeAsync(method, request, MessageType.Request, cancellationToken)
            .ConfigureAwait(false);
        var context = new MessageContext(
            envelope.Headers,
            envelope.Body,
            method.Channel,
            MessageType.Request,
            MessageDirection.Outbound,
            cancellationToken)
        {
            Message = request,
            Method = method,
            Services = _services
        };
        context.Items[DiagnosticContext.TransportNameKey] = registration.TransportName;
        envelope.Headers[HeaderNames.Deadline] = DateTimeOffset.UtcNow
            .Add(registration.Timeout)
            .ToString("O");
        var measurement = AnyProtocolDiagnostics.StartOperation(
            context,
            registration.TransportName,
            "stream");
        using var activity = TracingFilter.StartActivity(context);
        var outcome = "cancelled";
        try
        {
            var stream = transport is INativeStreamingTransport streamingTransport
                ? streamingTransport.StreamAsync(method.Channel, envelope, cancellationToken)
                : _streamEngines.GetOrAdd(transport, static value => new StreamEngine(value))
                    .StreamAsync(method.Channel, envelope, registration.Timeout, cancellationToken);
            await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(exception, cancellationToken);
                    TracingFilter.SetError(activity, exception, cancellationToken);
                    throw;
                }

                if (!hasNext)
                {
                    outcome = "success";
                    activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                    yield break;
                }

                var item = enumerator.Current;
                try
                {
                    ThrowIfFault(item);
                }
                catch (Exception exception)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(exception, cancellationToken);
                    TracingFilter.SetError(activity, exception, cancellationToken);
                    throw;
                }

                if (item.Headers.Get(HeaderNames.MessageType, MessageType.StreamItem) ==
                    MessageType.StreamComplete)
                {
                    outcome = "success";
                    activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                    yield break;
                }

                TItem? value;
                try
                {
                    value = await _serializer.DeserializeAsync<TItem>(
                            item.Body,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(exception, cancellationToken);
                    TracingFilter.SetError(activity, exception, cancellationToken);
                    throw;
                }

                if (value is not null)
                {
                    yield return value;
                }
            }
        }
        finally
        {
            measurement.Complete(outcome);
        }
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _engines.Values)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var engine in _streamEngines.Values)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<MessageContext> CreateContextAsync<TRequest>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var messageType = method.Operation == ContractOperation.Send && !method.ExpectReply
            ? MessageType.Event
            : MessageType.Request;
        var envelope = await CreateEnvelopeAsync(method, request, messageType, cancellationToken)
            .ConfigureAwait(false);
        var context = new MessageContext(
            envelope.Headers,
            envelope.Body,
            method.Channel,
            messageType,
            MessageDirection.Outbound,
            cancellationToken)
        {
            Message = request,
            Method = method,
            Services = _services
        };
        context.Items[DiagnosticContext.TransportNameKey] =
            GetRegistration(method.ContractType).TransportName;
        return context;
    }

    private async ValueTask<TransportEnvelope> CreateEnvelopeAsync<TRequest>(
        ContractMethodDescriptor method,
        TRequest request,
        MessageType messageType,
        CancellationToken cancellationToken)
    {
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.Channel] = method.Channel,
            [HeaderNames.Contract] = method.ContractName,
            [HeaderNames.Method] = method.MethodName,
            [HeaderNames.MessageType] = messageType.ToString(),
            [HeaderNames.ContentType] = ContentType,
            [HeaderNames.SentAt] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (method.PartitionKeyProperty is not null)
        {
            var value = request is null ? null : method.PartitionKeyProperty.GetValue(request);
            var partitionKey = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(partitionKey))
            {
                throw new InvalidOperationException(
                    $"Partition key '{method.RequestType.FullName}.{method.PartitionKeyProperty.Name}' " +
                    "must not be null or empty.");
            }

            headers[HeaderNames.PartitionKey] = partitionKey;
        }

        var body = await _serializer.SerializeAsync(request, cancellationToken).ConfigureAwait(false);
        return new TransportEnvelope(headers, body);
    }

    private async ValueTask InvokeTransportAsync(IMessageContext context)
    {
        var contractName = context.Headers[HeaderNames.Contract]!;
        var registration = _registrations.Values.Single(
            candidate => (candidate.ContractType.FullName ?? candidate.ContractType.Name) == contractName);
        var transport = _transports.GetRequired(registration.TransportName);
        var envelope = new TransportEnvelope(context.Headers, context.Body);
        context.Headers[HeaderNames.Deadline] ??= DateTimeOffset.UtcNow
            .Add(registration.Timeout)
            .ToString("O");

        if (context.MessageType == MessageType.Event)
        {
            await transport.SendAsync(context.Channel, envelope, context.CancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var engine = _engines.GetOrAdd(transport, static value => new RequestReplyEngine(value));
        var retry = new RetryFilter(
            new RetryOptions
            {
                MaxAttempts = registration.MaxRetryAttempts,
                MaxTotalTime = registration.Timeout
            });
        await retry.InvokeAsync(
                context,
                async retryContext =>
                {
                    retryContext.Response = await engine.RequestAsync(
                            retryContext.Channel,
                            envelope,
                            registration.Timeout,
                            retryContext.CancellationToken)
                        .ConfigureAwait(false);
                    ThrowIfFault(retryContext.Response);
                })
            .ConfigureAwait(false);
    }

    private ClientRegistration GetRegistration(Type contractType)
        => _registrations.TryGetValue(contractType, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"No client registration exists for contract '{contractType.FullName}'.");

    private void ThrowIfFault(TransportEnvelope envelope)
    {
        if (envelope.Headers.Get(HeaderNames.MessageType, MessageType.Response) != MessageType.Fault)
        {
            return;
        }

        var fault = _serializer.Deserialize<FaultMessage>(envelope.Body) ??
                    new FaultMessage("remote_fault", "The remote endpoint returned an empty fault.");
        if (string.Equals(fault.Code, "validation_failed", StringComparison.Ordinal))
        {
            throw new AnyProtocolValidationException(fault);
        }

        throw new AnyProtocolFaultException(fault);
    }
}
