using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using System.Text;

namespace AnyProtocol.Configuration;

/// <summary>
/// Builds the protocols, contracts, filters, and services used by a AnyProtocol bus.
/// </summary>
public sealed class LinkBuilder
{
    private readonly LinkConfiguration _configuration = new();

    /// <summary>Sets the serializer shared by clients, servers, and event handlers.</summary>
    public LinkBuilder UseSerializer(IMessageSerializer serializer)
    {
        _configuration.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        return this;
    }

    /// <summary>Registers a named messaging transport.</summary>
    public LinkBuilder AddTransport(ProtocolKey protocol, IMessagingProtocol transport)
    {
        if (string.IsNullOrWhiteSpace(protocol.Value))
        {
            throw new ArgumentException("A protocol key is required.", nameof(protocol));
        }

        ArgumentNullException.ThrowIfNull(transport);
        if (!_configuration.Transports.TryAdd(protocol, transport))
        {
            throw new InvalidOperationException($"Protocol '{protocol}' is already registered.");
        }

        return this;
    }

    /// <summary>Registers a named messaging transport.</summary>
    public LinkBuilder AddTransport(string name, IMessagingProtocol transport)
        => AddTransport(ProtocolKey.Create(name), transport);

    /// <summary>Registers a generated client proxy for a contract.</summary>
    public LinkBuilder AddClient<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
        TContract>(Action<ClientOptionsBuilder>? configure = null)
        where TContract : class
        => AddClient<TContract>(GetDefaultConfigurationKey(typeof(TContract)), configure);

    /// <summary>Registers a generated client proxy with a stable configuration key.</summary>
    public LinkBuilder AddClient<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
        TContract>(
        string configurationKey,
        Action<ClientOptionsBuilder>? configure = null)
        where TContract : class
    {
        ValidateConfigurationKey(configurationKey);
        EnsureUniqueConfigurationKey(_configuration.Clients, configurationKey, "client");
        var options = new ClientOptionsBuilder();
        configure?.Invoke(options);
        _configuration.Clients.Add(
            new ClientRegistration(
                typeof(TContract),
                options.Protocol,
                options.Timeout,
                options.MaxRetryAttempts)
            {
                ConfigurationKey = configurationKey,
                RequiredOrdering = options.RequiredOrdering
            });
        return this;
    }

    /// <summary>Registers a scoped server implementation for a contract.</summary>
    public LinkBuilder AddServer<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
        TContract,
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        TImplementation>(
        Action<ServerOptionsBuilder>? configure = null)
        where TContract : class
        where TImplementation : class, TContract
        => AddServer<TContract, TImplementation>(
            GetDefaultConfigurationKey(typeof(TContract)),
            configure);

    /// <summary>Registers a scoped server implementation with a stable configuration key.</summary>
    public LinkBuilder AddServer<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]
        TContract,
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        TImplementation>(
        string configurationKey,
        Action<ServerOptionsBuilder>? configure = null)
        where TContract : class
        where TImplementation : class, TContract
    {
        ValidateConfigurationKey(configurationKey);
        EnsureUniqueConfigurationKey(_configuration.Servers, configurationKey, "server");
        var options = new ServerOptionsBuilder();
        configure?.Invoke(options);
        _configuration.Servers.Add(
            new ServerRegistration(
                typeof(TContract),
                typeof(TImplementation),
                options.Protocols.ToArray())
            {
                ConfigurationKey = configurationKey,
                RequiredOrdering = options.RequiredOrdering
            });
        return this;
    }

    /// <summary>Overrides the protocol of a named client registration.</summary>
    public LinkBuilder ConfigureClientProtocol(string configurationKey, ProtocolKey protocol)
    {
        ValidateConfigurationKey(configurationKey);
        var index = _configuration.Clients.FindIndex(
            registration => string.Equals(
                registration.ConfigurationKey,
                configurationKey,
                StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Protocol configuration references unknown client registration '{configurationKey}'.");
        }

        if (string.IsNullOrWhiteSpace(protocol.Value))
        {
            throw new ArgumentException("A protocol key is required.", nameof(protocol));
        }

        _configuration.Clients[index] = _configuration.Clients[index] with { Protocol = protocol };
        return this;
    }

    /// <summary>Overrides the protocols of a named server registration.</summary>
    public LinkBuilder ConfigureServerProtocols(
        string configurationKey,
        params ProtocolKey[] protocols)
    {
        ValidateConfigurationKey(configurationKey);
        ArgumentNullException.ThrowIfNull(protocols);
        if (protocols.Length == 0 || protocols.Any(protocol =>
                string.IsNullOrWhiteSpace(protocol.Value)))
        {
            throw new ArgumentException(
                "At least one non-empty protocol key is required.",
                nameof(protocols));
        }

        var index = _configuration.Servers.FindIndex(
            registration => string.Equals(
                registration.ConfigurationKey,
                configurationKey,
                StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Protocol configuration references unknown server registration '{configurationKey}'.");
        }

        _configuration.Servers[index] = _configuration.Servers[index] with
        {
            Protocols = protocols.ToArray()
        };
        return this;
    }

    /// <summary>Registers an event consumer and its publisher.</summary>
    public LinkBuilder AddEventHandler<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        TEvent,
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        THandler>(
        Action<EventOptionsBuilder>? configure = null)
        where TEvent : class
        where THandler : class, IEventConsumer<TEvent>
    {
        var options = new EventOptionsBuilder(
            $"anyprotocol.event.{ToKebabCase(typeof(TEvent).Name)}");
        configure?.Invoke(options);
        _configuration.Events.Add(
            new EventRegistration(
                typeof(TEvent),
                typeof(THandler),
                options.Protocol,
                options.Channel,
                options.ConsumerGroupName)
            {
                RequiredOrdering = options.RequiredOrdering
            });
        return this;
    }

    /// <summary>Adds a filter to the outbound client pipeline.</summary>
    public LinkBuilder AddClientFilter(IMessageFilter filter)
    {
        _configuration.ClientFilters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
        return this;
    }

    /// <summary>Adds a filter to the inbound server pipeline.</summary>
    public LinkBuilder AddServerFilter(IMessageFilter filter)
    {
        _configuration.ServerFilters.Add(filter ?? throw new ArgumentNullException(nameof(filter)));
        return this;
    }

    /// <summary>Builds and validates the complete link configuration.</summary>
    public LinkConfiguration Build(ContractDescriptorFactory? descriptorFactory = null)
    {
        _configuration.Validate(descriptorFactory ?? new ContractDescriptorFactory());
        return _configuration;
    }

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(value[index]));
        }

        return builder.ToString();
    }

    private static string GetDefaultConfigurationKey(Type contractType)
        => contractType.FullName ?? contractType.Name;

    private static void ValidateConfigurationKey(string configurationKey)
        => ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);

    private static void EnsureUniqueConfigurationKey<TRegistration>(
        IEnumerable<TRegistration> registrations,
        string configurationKey,
        string registrationKind)
        where TRegistration : ContractRegistration
    {
        if (registrations.Any(registration => string.Equals(
                registration.ConfigurationKey,
                configurationKey,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A {registrationKind} registration with configuration key " +
                $"'{configurationKey}' already exists.");
        }
    }
}

/// <summary>
/// Provides the client options builder implementation used by AnyProtocol applications.
/// </summary>
public sealed class ClientOptionsBuilder
{
    internal ProtocolKey Protocol { get; private set; } = ProtocolKey.Default;

    internal TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(30);

    internal int MaxRetryAttempts { get; private set; } = 1;

    internal TransportOrdering RequiredOrdering { get; private set; }

    /// <summary>
    /// Performs the use transport operation.
    /// </summary>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the use transport operation.</returns>
    public ClientOptionsBuilder UseTransport(string name)
        => UseProtocol(ProtocolKey.Create(name));

    /// <summary>
    /// Performs the use protocol operation.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the use protocol operation.</returns>
    public ClientOptionsBuilder UseProtocol(ProtocolKey protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol.Value))
        {
            throw new ArgumentException("A protocol key is required.", nameof(protocol));
        }

        Protocol = protocol;
        return this;
    }

    /// <summary>
    /// Performs the with timeout operation.
    /// </summary>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <returns>The result of the with timeout operation.</returns>
    public ClientOptionsBuilder WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Timeout = timeout;
        return this;
    }

    /// <summary>
    /// Performs the with retry operation.
    /// </summary>
    /// <param name="maxAttempts">The max attempts.</param>
    /// <returns>The result of the with retry operation.</returns>
    public ClientOptionsBuilder WithRetry(int maxAttempts)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        MaxRetryAttempts = maxAttempts;
        return this;
    }

    /// <summary>Requires the selected transport to guarantee the specified ordering.</summary>
    public ClientOptionsBuilder RequireOrdering(TransportOrdering ordering)
    {
        RequiredOrdering = ordering;
        return this;
    }
}

/// <summary>
/// Provides the server options builder implementation used by AnyProtocol applications.
/// </summary>
public sealed class ServerOptionsBuilder
{
    internal IReadOnlyList<ProtocolKey> Protocols { get; private set; } =
        [ProtocolKey.Default];

    internal TransportOrdering RequiredOrdering { get; private set; }

    /// <summary>
    /// Performs the use transport operation.
    /// </summary>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the use transport operation.</returns>
    public ServerOptionsBuilder UseTransport(string name)
        => UseProtocols(ProtocolKey.Create(name));

    /// <summary>
    /// Performs the use protocol operation.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the use protocol operation.</returns>
    public ServerOptionsBuilder UseProtocol(ProtocolKey protocol)
        => UseProtocols(protocol);

    /// <summary>
    /// Performs the use protocols operation.
    /// </summary>
    /// <param name="protocols">The protocols.</param>
    /// <returns>The result of the use protocols operation.</returns>
    public ServerOptionsBuilder UseProtocols(params ProtocolKey[] protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        if (protocols.Length == 0 || protocols.Any(protocol =>
                string.IsNullOrWhiteSpace(protocol.Value)))
        {
            throw new ArgumentException(
                "At least one non-empty protocol key is required.",
                nameof(protocols));
        }

        Protocols = protocols.ToArray();
        return this;
    }

    /// <summary>Requires the selected transport to guarantee the specified ordering.</summary>
    public ServerOptionsBuilder RequireOrdering(TransportOrdering ordering)
    {
        RequiredOrdering = ordering;
        return this;
    }
}

/// <summary>
/// Provides the event options builder implementation used by AnyProtocol applications.
/// </summary>
/// <param name="defaultChannel">The default channel.</param>
public sealed class EventOptionsBuilder(string defaultChannel)
{
    internal ProtocolKey Protocol { get; private set; } = ProtocolKey.Default;

    internal string Channel { get; private set; } = defaultChannel;

    internal string? ConsumerGroupName { get; private set; }

    internal TransportOrdering RequiredOrdering { get; private set; }

    /// <summary>
    /// Performs the use transport operation.
    /// </summary>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the use transport operation.</returns>
    public EventOptionsBuilder UseTransport(string name)
        => UseProtocol(ProtocolKey.Create(name));

    /// <summary>
    /// Performs the use protocol operation.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the use protocol operation.</returns>
    public EventOptionsBuilder UseProtocol(ProtocolKey protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol.Value))
        {
            throw new ArgumentException("A protocol key is required.", nameof(protocol));
        }

        Protocol = protocol;
        return this;
    }

    /// <summary>
    /// Performs the use channel operation.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <returns>The result of the use channel operation.</returns>
    public EventOptionsBuilder UseChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        Channel = channel;
        return this;
    }

    /// <summary>
    /// Performs the consumer group operation.
    /// </summary>
    /// <param name="consumerGroup">The consumer group.</param>
    /// <returns>The result of the consumer group operation.</returns>
    public EventOptionsBuilder ConsumerGroup(string consumerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ConsumerGroupName = consumerGroup;
        return this;
    }

    /// <summary>Requires the selected transport to guarantee the specified ordering.</summary>
    public EventOptionsBuilder RequireOrdering(TransportOrdering ordering)
    {
        RequiredOrdering = ordering;
        return this;
    }
}
