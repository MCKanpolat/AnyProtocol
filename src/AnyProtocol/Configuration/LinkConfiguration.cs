using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using System.Runtime.CompilerServices;

namespace AnyProtocol.Configuration;

/// <summary>
/// Stores mutable link composition state while a <see cref="LinkBuilder"/> is being configured.
/// </summary>
internal sealed class LinkDefinition
{
    internal Dictionary<ProtocolKey, IMessagingProtocol> Transports { get; } = [];

    internal List<ClientRegistration> Clients { get; } = [];

    internal List<ServerRegistration> Servers { get; } = [];

    internal List<EventRegistration> Events { get; } = [];

    internal List<IMessageFilter> ClientFilters { get; } = [];

    internal List<IMessageFilter> ServerFilters { get; } = [];

    internal IMessageSerializer? Serializer { get; set; }

    internal LargePayloadOffloadOptions? LargePayloadOffload { get; set; }

    internal void Validate(ContractDescriptorFactory descriptorFactory)
    {
        if (Serializer is null)
        {
            throw new InvalidOperationException("A message serializer must be configured.");
        }

        ValidateLargePayloadOffload();

        foreach (var registration in Clients)
        {
            var descriptor = descriptorFactory.CreateRuntimeCompatible(registration.ContractType);
            ValidateGeneratedContract(registration.ContractType, Serializer);
            ValidateContractBinding(registration, registration.Protocol, descriptor.Methods, false);
        }

        foreach (var registration in Servers)
        {
            ValidateGeneratedContract(registration.ContractType, Serializer);
            var duplicateProtocol = registration.Protocols
                .GroupBy(static protocol => protocol)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateProtocol is not null)
            {
                throw new InvalidOperationException(
                    $"Contract '{registration.ContractType.FullName}' selects protocol " +
                    $"'{duplicateProtocol.Key}' more than once.");
            }

            var duplicateNativeType = registration.Protocols
                .Select(protocol =>
                {
                    Transports.TryGetValue(protocol, out var transport);
                    return (Protocol: protocol, Transport: transport);
                })
                .Where(static binding => binding.Transport is INativeServerTransport)
                .GroupBy(static binding => binding.Transport!.GetType())
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateNativeType is not null)
            {
                var protocols = string.Join(
                    ", ",
                    duplicateNativeType.Select(static binding => $"'{binding.Protocol}'"));
                throw new InvalidOperationException(
                    $"Contract '{registration.ContractType.FullName}' selects native server " +
                    $"transport type '{duplicateNativeType.Key.FullName}' more than once via " +
                    $"protocols {protocols}. A contract can select each native server type only once.");
            }

            var descriptor = descriptorFactory.CreateRuntimeCompatible(registration.ContractType);
            foreach (var protocol in registration.Protocols)
            {
                if (protocol == ProtocolKey.Mcp)
                {
                    continue;
                }

                ValidateContractBinding(registration, protocol, descriptor.Methods, true);
            }
        }

        foreach (var registration in Events)
        {
            if (!Transports.TryGetValue(registration.Protocol, out var transport))
            {
                throw new InvalidOperationException(
                    $"Event '{registration.EventType.FullName}' references unknown transport " +
                    $"'{registration.Protocol}'.");
            }

            if (transport is not INativeServerTransport)
            {
                RequireInterface<EventRegistration, ISendTransport>(
                    registration,
                    transport,
                    "event publishing");
                RequireInterface<EventRegistration, ISubscriptionTransport>(
                    registration,
                    transport,
                    "event subscriptions");
            }

            if (registration.ConsumerGroup is not null)
            {
                if (transport is INativeServerTransport)
                {
                    throw new InvalidOperationException(
                        $"Event '{registration.EventType.FullName}' configures a consumer group, " +
                        $"but native server transport '{registration.Protocol}' does not use " +
                        "subscriptions.");
                }

                RequireCapability(
                    registration,
                    transport,
                    TransportCapabilities.CompetingConsumers,
                    "event consumer groups");
            }

            var partitionKeys = GetPartitionKeys(registration.EventType);
            if (partitionKeys.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Event '{registration.EventType.FullName}' contains more than one " +
                    "[PartitionKey] property.");
            }

            if (partitionKeys.Length == 1 && !transport.Semantics.SupportsPartitioning)
            {
                throw new InvalidOperationException(
                    $"Event '{registration.EventType.FullName}' uses [PartitionKey], but transport " +
                    $"'{registration.Protocol}' does not support partitioning.");
            }

            if (partitionKeys is [{ CanRead: false }] ||
                partitionKeys is [{ } partitionKey] &&
                partitionKey.GetIndexParameters().Length != 0)
            {
                throw new InvalidOperationException(
                    $"Event [PartitionKey] property '{partitionKeys[0].Name}' must be readable " +
                    "and non-indexed.");
            }

            ValidateOrdering(
                registration.EventType.FullName ?? registration.EventType.Name,
                registration.Protocol.Value,
                registration.RequiredOrdering,
                partitionKeys.Length == 1,
                transport.Semantics);
        }

        var duplicateRoute = Servers
            .SelectMany(
                registration => registration.Protocols.SelectMany(
                    protocol => descriptorFactory.CreateRuntimeCompatible(registration.ContractType).Methods
                    .Select(method => new
                    {
                        Protocol = protocol,
                        method.Channel,
                        method.ContractName,
                        method.MethodName
                    })))
            .GroupBy(route => (route.Protocol, route.Channel, route.ContractName, route.MethodName))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRoute is not null)
        {
            throw new InvalidOperationException(
                $"Server route '{duplicateRoute.Key.ContractName}.{duplicateRoute.Key.MethodName}' " +
                $"is registered more than once on protocol '{duplicateRoute.Key.Protocol}'.");
        }
    }

    private void ValidateLargePayloadOffload()
    {
        if (LargePayloadOffload is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(LargePayloadOffload.StoreName);
        if (LargePayloadOffload.MaxInlinePayloadBytes <= 0)
        {
            throw new InvalidOperationException(
                "MaxInlinePayloadBytes must be greater than zero when large-payload offload is enabled.");
        }

        if (LargePayloadOffload.MaxStoredPayloadBytes <=
            LargePayloadOffload.MaxInlinePayloadBytes)
        {
            throw new InvalidOperationException(
                "MaxStoredPayloadBytes must be greater than MaxInlinePayloadBytes.");
        }

        if (LargePayloadOffload.TimeToLive <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "TimeToLive must be greater than zero when large-payload offload is enabled.");
        }
    }

    private static void ValidateGeneratedContract(Type contractType, IMessageSerializer serializer)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            GeneratedContractRegistry.Validate(contractType, serializer);
        }
    }

    private void ValidateContractBinding(
        ContractRegistration registration,
        ProtocolKey protocol,
        IReadOnlyList<ContractMethodDescriptor> methods,
        bool isServer)
    {
        if (!Transports.TryGetValue(protocol, out var transport))
        {
            throw new InvalidOperationException(
                $"Contract '{registration.ContractType.FullName}' references unknown protocol " +
                $"'{protocol}'.");
        }

        ValidateOrdering(
            registration.ContractType.FullName ?? registration.ContractType.Name,
            protocol.Value,
            registration.RequiredOrdering,
            methods.All(method => method.PartitionKeyProperty is not null),
            transport.Semantics);

        if (isServer && transport is not INativeServerTransport)
        {
            RequireInterface<ContractRegistration, ISubscriptionTransport>(
                registration,
                transport,
                "server subscriptions",
                protocol);
            RequireInterface<ContractRegistration, ISendTransport>(
                registration,
                transport,
                "server responses",
                protocol);
            RequireCapability(registration, protocol, transport,
                TransportCapabilities.CompetingConsumers, "competing server consumers");
        }

        if (!isServer && methods.Any(method =>
                method.Operation == ContractOperation.Send && !method.ExpectReply))
        {
            RequireInterface<ContractRegistration, ISendTransport>(
                registration,
                transport,
                "client sends",
                protocol);
        }

        if (!isServer &&
            methods.Any(method =>
                method.Operation == ContractOperation.Request || method.ExpectReply) &&
            transport is not IRequestReplyTransport)
        {
            RequireInterface<ContractRegistration, ISubscriptionTransport>(
                registration,
                transport,
                "request/reply emulation subscriptions",
                protocol);
            RequireInterface<ContractRegistration, ISendTransport>(
                registration,
                transport,
                "request/reply emulation sends",
                protocol);
        }

        if (!isServer &&
            methods.Any(method => method.Operation == ContractOperation.Stream) &&
            transport is not IStreamingTransport &&
            (transport is not ISubscriptionTransport || transport is not ISendTransport))
        {
            throw new InvalidOperationException(
                $"Contract '{registration.ContractType.FullName}' contains streaming methods, " +
                $"but protocol '{protocol}' supports neither native streaming nor " +
                "publish/subscribe stream emulation.");
        }

        if (!isServer &&
            methods.Any(method => method.Operation == ContractOperation.Stream) &&
            transport is not IStreamingTransport)
        {
            RequireInterface<ContractRegistration, ISubscriptionTransport>(
                registration,
                transport,
                "stream emulation subscriptions",
                protocol);
            RequireInterface<ContractRegistration, ISendTransport>(
                registration,
                transport,
                "stream emulation sends",
                protocol);
        }

        var partitionedMethod = methods.FirstOrDefault(
            method => method.PartitionKeyProperty is not null);
        if (partitionedMethod is not null && !transport.Semantics.SupportsPartitioning)
        {
            throw new InvalidOperationException(
                $"Contract '{registration.ContractType.FullName}' method " +
                $"'{partitionedMethod.MethodName}' uses [PartitionKey], but protocol " +
                $"'{protocol}' does not support partitioning.");
        }
    }

    private static void RequireCapability(
        ContractRegistration registration,
        ProtocolKey protocol,
        IMessagingProtocol transport,
        TransportCapabilities capability,
        string feature)
    {
        if (!transport.Capabilities.HasFlag(capability))
        {
            throw new InvalidOperationException(
                $"Contract '{registration.ContractType.FullName}' requires {feature}, but transport " +
                $"'{protocol}' does not support {capability}.");
        }
    }

    private static void RequireCapability(
        EventRegistration registration,
        IMessagingProtocol transport,
        TransportCapabilities capability,
        string feature)
    {
        if (!transport.Capabilities.HasFlag(capability))
        {
            throw new InvalidOperationException(
                $"Event '{registration.EventType.FullName}' requires {feature}, but transport " +
                $"'{registration.Protocol}' does not support {capability}.");
        }
    }

    private static void RequireInterface<TRegistration, TCapability>(
        TRegistration registration,
        IMessagingProtocol transport,
        string feature,
        ProtocolKey? protocol = null)
        where TRegistration : class
        where TCapability : class
    {
        if (transport is TCapability)
        {
            return;
        }

        var registrationName = registration switch
        {
            ContractRegistration contract => contract.ContractType.FullName ?? contract.ContractType.Name,
            EventRegistration @event => @event.EventType.FullName ?? @event.EventType.Name,
            _ => typeof(TRegistration).Name
        };
        var transportName = protocol?.Value ??
            (registration is EventRegistration eventRegistration
                ? eventRegistration.Protocol.Value
                : "the configured transport");
        throw new InvalidOperationException(
            $"'{registrationName}' requires {feature}, but transport '{transportName}' " +
            $"does not implement {typeof(TCapability).Name}.");
    }

    private static void ValidateOrdering(
        string registrationName,
        string transportName,
        TransportOrdering required,
        bool allOperationsPartitioned,
        TransportSemantics semantics)
    {
        if (required == TransportOrdering.None)
        {
            return;
        }

        if (semantics.Ordering != required)
        {
            throw new InvalidOperationException(
                $"'{registrationName}' requires {required} ordering, but transport " +
                $"'{transportName}' guarantees {semantics.Ordering} ordering.");
        }

        if (required == TransportOrdering.PerPartition && !allOperationsPartitioned)
        {
            throw new InvalidOperationException(
                $"'{registrationName}' requires per-partition ordering, but every operation must " +
                "declare a [PartitionKey] property.");
        }
    }

    private static System.Reflection.PropertyInfo[] GetPartitionKeys(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Instance |
                              System.Reflection.BindingFlags.Public)
            .Where(property => property.IsDefined(typeof(PartitionKeyAttribute), inherit: true))
            .ToArray();
}

/// <summary>
/// Provides the contract registration implementation used by AnyProtocol applications.
/// </summary>
/// <param name="ContractType">The contract type.</param>
public abstract record ContractRegistration(
    [property: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
    Type ContractType)
{
    /// <summary>
    /// Gets or initializes the configuration key.
    /// </summary>
    /// <value>The configuration key.</value>
    public string ConfigurationKey { get; init; } = ContractType.FullName ?? ContractType.Name;

    /// <summary>
    /// Gets or initializes the required ordering.
    /// </summary>
    /// <value>The required ordering.</value>
    public TransportOrdering RequiredOrdering { get; init; }
}

/// <summary>
/// Provides the client registration implementation used by AnyProtocol applications.
/// </summary>
/// <param name="ContractType">The contract type.</param>
/// <param name="Protocol">The protocol registration key.</param>
/// <param name="Timeout">The maximum time allowed for the operation.</param>
/// <param name="MaxRetryAttempts">The max retry attempts.</param>
public sealed record ClientRegistration(
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
    Type ContractType,
    ProtocolKey Protocol,
    TimeSpan Timeout,
    int MaxRetryAttempts)
    : ContractRegistration(ContractType)
{
    /// <summary>
    /// Initializes a new instance of the ClientRegistration class.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="transportName">The transport name.</param>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <param name="maxRetryAttempts">The max retry attempts.</param>
    public ClientRegistration(
        Type contractType,
        string transportName,
        TimeSpan timeout,
        int maxRetryAttempts)
        : this(contractType, ProtocolKey.Create(transportName), timeout, maxRetryAttempts)
    {
    }

    /// <summary>
    /// Gets the transport name.
    /// </summary>
    /// <value>The transport name.</value>
    public string TransportName => Protocol.Value;
}

/// <summary>
/// Provides the server registration implementation used by AnyProtocol applications.
/// </summary>
/// <param name="ContractType">The contract type.</param>
/// <param name="ImplementationType">The implementation type.</param>
/// <param name="Protocols">The protocols.</param>
public sealed record ServerRegistration(
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
    Type ContractType,
    [property: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type ImplementationType,
    IReadOnlyList<ProtocolKey> Protocols)
    : ContractRegistration(ContractType)
{
    /// <summary>
    /// Initializes a new instance of the ServerRegistration class.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="implementationType">The implementation type.</param>
    /// <param name="transportName">The transport name.</param>
    public ServerRegistration(Type contractType, Type implementationType, string transportName)
        : this(
            contractType,
            implementationType,
            new[] { ProtocolKey.Create(transportName) })
    {
    }

    /// <summary>
    /// Gets the transport name.
    /// </summary>
    /// <value>The transport name.</value>
    public string TransportName => Protocols[0].Value;
}

/// <summary>
/// Provides the event registration implementation used by AnyProtocol applications.
/// </summary>
/// <param name="EventType">The event type.</param>
/// <param name="HandlerType">The handler type.</param>
/// <param name="Protocol">The protocol registration key.</param>
/// <param name="Channel">The logical message channel.</param>
/// <param name="ConsumerGroup">The consumer group.</param>
public sealed record EventRegistration(
    [property: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    Type EventType,
    [property: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
    [param: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type HandlerType,
    ProtocolKey Protocol,
    string Channel,
    string? ConsumerGroup)
{
    /// <summary>
    /// Initializes a new instance of the EventRegistration class.
    /// </summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="handlerType">The handler type.</param>
    /// <param name="transportName">The transport name.</param>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="consumerGroup">The consumer group.</param>
    public EventRegistration(
        Type eventType,
        Type handlerType,
        string transportName,
        string channel,
        string? consumerGroup)
        : this(
            eventType,
            handlerType,
            ProtocolKey.Create(transportName),
            channel,
            consumerGroup)
    {
    }

    /// <summary>
    /// Gets the transport name.
    /// </summary>
    /// <value>The transport name.</value>
    public string TransportName => Protocol.Value;

    /// <summary>
    /// Gets or initializes the required ordering.
    /// </summary>
    /// <value>The required ordering.</value>
    public TransportOrdering RequiredOrdering { get; init; }
}
