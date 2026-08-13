using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Services;
using AnyProtocol.Storage.Abstraction;

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
    private readonly OutboundOperationExecutor _executor;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly LargePayloadOffloader _payloadOffloader;
    private readonly LargePayloadMaterializer _payloadMaterializer;
    private readonly MessageFilterDelegate _streamPreparationPipeline;
    private readonly ConcurrentDictionary<IMessagingProtocol, RequestReplyEngine> _engines = new();
    private readonly ConcurrentDictionary<IMessagingProtocol, StreamEngine> _streamEngines = new();

    /// <summary>
    /// Initializes a new instance of the AnyProtocolClientInvoker class.
    /// </summary>
    /// <param name="transports">The transports.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="registrations">The registrations.</param>
    /// <param name="executor">The outbound operation executor.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="payloadOffloader">The optional outbound payload processor.</param>
    /// <param name="payloadMaterializer">The optional inbound payload processor.</param>
    public AnyProtocolClientInvoker(
        TransportRegistry transports,
        IMessageSerializer serializer,
        IEnumerable<ClientRegistration> registrations,
        OutboundOperationExecutor executor,
        IMessageEnvelopeFactory? envelopeFactory = null,
        LargePayloadOffloader? payloadOffloader = null,
        LargePayloadMaterializer? payloadMaterializer = null)
    {
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _registrations = registrations.ToDictionary(registration => registration.ContractType);
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
        var emptyStores = new LargePayloadStoreRegistry([]);
        _payloadOffloader = payloadOffloader ?? new LargePayloadOffloader(null, emptyStores);
        _payloadMaterializer = payloadMaterializer ?? new LargePayloadMaterializer(
            emptyStores,
            new DefaultDateTimeProvider());
        _pipeline = _executor.CreatePipeline(InvokeTransportAsync);
        _streamPreparationPipeline = _executor.CreatePipeline(
            static _ => ValueTask.CompletedTask,
            includeObservability: false);
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
        await _executor.ExecuteAsync(context, _pipeline).ConfigureAwait(false);
        var response = MessageContextRuntime.Get(context).Result.Response ??
                       throw new InvalidOperationException($"Method '{method.MethodName}' produced no response.");
        response = await _payloadMaterializer.MaterializeAsync(response, cancellationToken)
            .ConfigureAwait(false);
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
        await _executor.ExecuteAsync(context, _pipeline).ConfigureAwait(false);
        var response = MessageContextRuntime.Get(context).Result.Response;
        if (response is not null)
        {
            ThrowIfFault(response);
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
        var context = await CreateContextAsync(method, request, cancellationToken).ConfigureAwait(false);
        var measurement = AnyProtocolDiagnostics.StartOperation(
            context,
            registration.TransportName,
            "stream");
        using var activity = TracingFilter.StartActivity(context);
        var outcome = "cancelled";
        long totalBytes = 0;
        try
        {
            await foreach (var item in _executor.ExecuteStreamAsync(
                               context,
                               _streamPreparationPipeline,
                               current => CreateStreamResponses(
                                   current,
                                   method,
                                   registration,
                                   transport,
                                   current.CancellationToken),
                               cancellationToken)
                               .ConfigureAwait(false))
            {
                TransportEnvelope materializedItem;
                try
                {
                    materializedItem = await _payloadMaterializer.MaterializeAsync(
                            item,
                            cancellationToken)
                        .ConfigureAwait(false);
                    totalBytes += materializedItem.Body.Length;
                    if (_payloadOffloader.Policy is { } policy &&
                        totalBytes > policy.MaxStoredPayloadBytes)
                    {
                        throw new LargePayloadException(
                            LargePayloadFailureCodes.TooLarge,
                            $"The stream exceeded its configured total serialized size of " +
                            $"{policy.MaxStoredPayloadBytes} bytes.");
                    }
                    ThrowIfFault(materializedItem);
                }
                catch (Exception exception)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(exception, cancellationToken);
                    TracingFilter.SetError(activity, exception, cancellationToken);
                    throw;
                }

                if (materializedItem.Headers.Get(HeaderNames.MessageType, MessageType.StreamItem) ==
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
                            materializedItem.Body,
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
            outcome = "success";
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
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
            Invocation = new MessageInvocation(cancellationToken)
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
        var headers = _envelopeFactory.CreateOutboundHeaders(
            messageType,
            method.Channel,
            method.ContractName,
            method.MethodName,
            ContentType);
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

    private async IAsyncEnumerable<TransportEnvelope> CreateStreamResponses(
        IMessageContext context,
        ContractMethodDescriptor method,
        ClientRegistration registration,
        IMessagingProtocol transport,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var envelope = await _payloadOffloader.OffloadAsync(
                new TransportEnvelope(context.Headers, context.Body),
                cancellationToken)
            .ConfigureAwait(false);
        context.Headers[HeaderNames.Deadline] ??= _envelopeFactory.GetUtcNow()
            .Add(registration.Timeout)
            .ToString("O");
        var responses = transport is IStreamingTransport streamingTransport
            ? streamingTransport.StreamAsync(method.Channel, envelope, context.CancellationToken)
            : _streamEngines.GetOrAdd(
                    transport,
                    value => new StreamEngine(value, envelopeFactory: _envelopeFactory))
                .StreamAsync(method.Channel, envelope, registration.Timeout, context.CancellationToken);
        await foreach (var response in responses.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return response;
        }
    }

    private async ValueTask InvokeTransportAsync(IMessageContext context)
    {
        var contractName = context.Headers[HeaderNames.Contract]!;
        var registration = _registrations.Values.Single(
            candidate => (candidate.ContractType.FullName ?? candidate.ContractType.Name) == contractName);
        var transport = _transports.GetRequired(registration.TransportName);
        var envelope = await _payloadOffloader.OffloadAsync(
                new TransportEnvelope(context.Headers, context.Body),
                context.CancellationToken)
            .ConfigureAwait(false);
        context.Headers[HeaderNames.Deadline] ??= _envelopeFactory.GetUtcNow()
            .Add(registration.Timeout)
            .ToString("O");

        if (context.MessageType == MessageType.Event)
        {
            if (transport is IMethodAwareMessagingProtocol methodAwareTransport &&
                context.Method is not null)
            {
                await methodAwareTransport.SendAsync(
                        context.Channel,
                        envelope,
                        context.Method,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (transport is not ISendTransport sendTransport)
                {
                    throw new InvalidOperationException(
                        $"Transport '{registration.TransportName}' does not implement ISendTransport.");
                }

                await sendTransport.SendAsync(context.Channel, envelope, context.CancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var engine = _engines.GetOrAdd(
            transport,
            value => new RequestReplyEngine(value, _envelopeFactory));
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
                    MessageContextRuntime.Get(retryContext).Result.Response = await engine.RequestAsync(
                            retryContext.Channel,
                            envelope,
                            registration.Timeout,
                            retryContext.CancellationToken,
                            context.Method)
                        .ConfigureAwait(false);
                    ThrowIfFault(MessageContextRuntime.Get(retryContext).Result.Response!);
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
