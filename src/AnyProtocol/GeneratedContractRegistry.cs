using AnyProtocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Stores and resolves generated contract registrations.
/// </summary>
public static class GeneratedContractRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, Registration> Registrations = [];

    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    /// <param name="serializableTypes">The serializable types.</param>
    /// <param name="proxyFactory">The proxy factory.</param>
    public static void Register(
        Type contractType,
        Func<string, ContractDescriptor> descriptorFactory,
        IReadOnlyList<Type> serializableTypes,
        Func<IClientInvoker, ContractDescriptorFactory, object> proxyFactory)
        => RegisterCore(
            contractType,
            descriptorFactory,
            serializableTypes,
            serverMethods: null,
            proxyFactory);

    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    /// <param name="serializableTypes">The serializable types.</param>
    /// <param name="serverMethods">The server methods.</param>
    /// <param name="proxyFactory">The proxy factory.</param>
    public static void Register(
        Type contractType,
        Func<string, ContractDescriptor> descriptorFactory,
        IReadOnlyList<Type> serializableTypes,
        IReadOnlyList<GeneratedServerMethodRegistration> serverMethods,
        Func<IClientInvoker, ContractDescriptorFactory, object> proxyFactory)
    {
        ArgumentNullException.ThrowIfNull(serverMethods);
        RegisterCore(
            contractType,
            descriptorFactory,
            serializableTypes,
            CreateServerMethods(contractType, serverMethods),
            proxyFactory);
    }

    private static void RegisterCore(
        Type contractType,
        Func<string, ContractDescriptor> descriptorFactory,
        IReadOnlyList<Type> serializableTypes,
        IReadOnlyDictionary<string, GeneratedServerMethodRegistration>? serverMethods,
        Func<IClientInvoker, ContractDescriptorFactory, object> proxyFactory)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(descriptorFactory);
        ArgumentNullException.ThrowIfNull(serializableTypes);
        ArgumentNullException.ThrowIfNull(proxyFactory);
        lock (Sync)
        {
            if (Registrations.ContainsKey(contractType))
            {
                throw Duplicate(contractType, "generated registration");
            }

            Registrations.Add(
                contractType,
                new Registration(
                    descriptorFactory,
                    serializableTypes,
                    serverMethods,
                    proxyFactory));
        }
    }

    /// <summary>
    /// Performs the is complete operation.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool IsComplete(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration) &&
                   registration.IsComplete;
        }
    }

    /// <summary>
    /// Validates .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="serializer">The serializer.</param>
    public static void Validate(Type contractType, IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(serializer);

        Registration registration;
        lock (Sync)
        {
            if (!Registrations.TryGetValue(contractType, out registration!) ||
                !registration.IsComplete)
            {
                var missing = registration?.GetMissingComponents() ??
                              ["descriptor", "proxy", "serializer metadata", "server dispatch"];
                throw new InvalidOperationException(
                    $"Generated AnyProtocol registration for '{DisplayName(contractType)}' is " +
                    $"incomplete. Missing: {string.Join(", ", missing)}. Reference " +
                    "AnyProtocol.Generator and register the closed contract directly with " +
                    "LinkBuilder.AddClient<TContract>() or AddServer<TContract, TImplementation>().");
            }
        }

        var descriptor = registration.DescriptorFactory!("anyprotocol");
        var descriptorMethodNames = descriptor.Methods
            .Select(static method => method.MethodName)
            .ToHashSet(StringComparer.Ordinal);
        var invalidServerMethods = descriptor.Methods
            .Where(
                method =>
                    !registration.ServerMethods!.TryGetValue(
                        method.MethodName,
                        out var generatedMethod) ||
                    (method.Operation == ContractOperation.Stream
                        ? generatedMethod?.StreamHandler is null
                        : generatedMethod?.Handler is null))
            .Select(static method => method.MethodName)
            .Concat(
                registration.ServerMethods!.Keys.Where(
                    methodName => !descriptorMethodNames.Contains(methodName)))
            .OrderBy(static methodName => methodName, StringComparer.Ordinal)
            .ToArray();
        if (invalidServerMethods.Length > 0)
        {
            throw new InvalidOperationException(
                $"Generated server dispatch metadata for contract '{DisplayName(contractType)}' " +
                $"does not match its descriptor. Missing or invalid method(s): " +
                $"{string.Join(", ", invalidServerMethods)}.");
        }

        if (serializer is not IMessageSerializerMetadataProvider metadataProvider)
        {
            throw new InvalidOperationException(
                $"Serializer '{serializer.GetType().FullName}' cannot validate Native AOT metadata " +
                $"for contract '{DisplayName(contractType)}'. Use a serializer that implements " +
                $"{nameof(IMessageSerializerMetadataProvider)}.");
        }

        var unsupportedTypes = registration.SerializableTypes!
            .Where(type => !metadataProvider.SupportsType(type))
            .Select(DisplayName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (unsupportedTypes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Serializer '{serializer.GetType().FullName}' has no generated metadata for " +
                $"contract '{DisplayName(contractType)}' message type(s): " +
                $"{string.Join(", ", unsupportedTypes)}. Add those types to the serializer's " +
                "source-generated metadata.");
        }
    }

    internal static void RegisterDescriptor(
        Type contractType,
        Func<string, ContractDescriptor> factory)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
        {
            var registration = GetOrAdd(contractType);
            if (registration.DescriptorFactory is not null)
            {
                throw Duplicate(contractType, "descriptor");
            }

            registration.DescriptorFactory = factory;
        }
    }

    internal static void RegisterMetadata(
        Type contractType,
        IReadOnlyList<Type> serializableTypes)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(serializableTypes);
        lock (Sync)
        {
            var registration = GetOrAdd(contractType);
            if (registration.SerializableTypes is not null)
            {
                throw Duplicate(contractType, "serializer metadata");
            }

            registration.SerializableTypes = serializableTypes;
        }
    }

    internal static void RegisterProxy(
        Type contractType,
        Func<IClientInvoker, ContractDescriptorFactory, object> factory)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
        {
            var registration = GetOrAdd(contractType);
            if (registration.ProxyFactory is not null)
            {
                throw Duplicate(contractType, "proxy");
            }

            registration.ProxyFactory = factory;
        }
    }

    internal static bool IsDescriptorRegistered(Type contractType)
    {
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration) &&
                   registration.DescriptorFactory is not null;
        }
    }

    internal static bool IsMetadataRegistered(Type contractType)
    {
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration) &&
                   registration.SerializableTypes is not null;
        }
    }

    internal static bool IsProxyRegistered(Type contractType)
    {
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration) &&
                   registration.ProxyFactory is not null;
        }
    }

    internal static bool TryCreateDescriptor(
        Type contractType,
        string channelPrefix,
        out ContractDescriptor? descriptor)
    {
        Func<string, ContractDescriptor>? factory;
        lock (Sync)
        {
            factory = Registrations.TryGetValue(contractType, out var registration)
                ? registration.DescriptorFactory
                : null;
        }

        descriptor = factory?.Invoke(channelPrefix);
        return descriptor is not null;
    }

    internal static IReadOnlyList<Type> GetSerializableTypes(Type contractType)
    {
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration)
                ? registration.SerializableTypes ?? []
                : [];
        }
    }

    internal static bool TryCreateProxy(
        Type contractType,
        IClientInvoker invoker,
        ContractDescriptorFactory descriptorFactory,
        out object? proxy)
    {
        Func<IClientInvoker, ContractDescriptorFactory, object>? factory;
        lock (Sync)
        {
            factory = Registrations.TryGetValue(contractType, out var registration)
                ? registration.ProxyFactory
                : null;
        }

        proxy = factory?.Invoke(invoker, descriptorFactory);
        return proxy is not null;
    }

    internal static bool IsServerMethodRegistered(Type contractType, string methodName)
    {
        lock (Sync)
        {
            return Registrations.TryGetValue(contractType, out var registration) &&
                   registration.ServerMethods?.ContainsKey(methodName) == true;
        }
    }

    internal static bool TryGetServerHandler(
        Type contractType,
        string methodName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out GeneratedServerHandler? handler)
    {
        lock (Sync)
        {
            handler = Registrations.TryGetValue(contractType, out var registration) &&
                      registration.ServerMethods?.TryGetValue(methodName, out var method) == true
                ? method.Handler
                : null;
        }

        return handler is not null;
    }

    internal static bool TryGetServerStreamHandler(
        Type contractType,
        string methodName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out GeneratedServerStreamHandler? handler)
    {
        lock (Sync)
        {
            handler = Registrations.TryGetValue(contractType, out var registration) &&
                      registration.ServerMethods?.TryGetValue(methodName, out var method) == true
                ? method.StreamHandler
                : null;
        }

        return handler is not null;
    }

    private static Registration GetOrAdd(Type contractType)
    {
        if (!Registrations.TryGetValue(contractType, out var registration))
        {
            registration = new Registration();
            Registrations.Add(contractType, registration);
        }

        return registration;
    }

    private static InvalidOperationException Duplicate(Type contractType, string component)
        => new(
            $"A generated AnyProtocol {component} for '{DisplayName(contractType)}' is already " +
            "registered. Ensure the contract is generated by only one referenced assembly.");

    private static string DisplayName(Type type) => type.FullName ?? type.Name;

    private static IReadOnlyDictionary<string, GeneratedServerMethodRegistration>
        CreateServerMethods(
            Type contractType,
            IReadOnlyList<GeneratedServerMethodRegistration> serverMethods)
    {
        var methods = new Dictionary<string, GeneratedServerMethodRegistration>(
            StringComparer.Ordinal);
        foreach (var method in serverMethods)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (!methods.TryAdd(method.MethodName, method))
            {
                throw new InvalidOperationException(
                    $"Generated server dispatch for '{DisplayName(contractType)}.{method.MethodName}' " +
                    "is registered more than once.");
            }
        }

        return methods;
    }

    private sealed class Registration
    {
        public Registration()
        {
        }

        public Registration(
            Func<string, ContractDescriptor> descriptorFactory,
            IReadOnlyList<Type> serializableTypes,
            IReadOnlyDictionary<string, GeneratedServerMethodRegistration>? serverMethods,
            Func<IClientInvoker, ContractDescriptorFactory, object> proxyFactory)
        {
            DescriptorFactory = descriptorFactory;
            SerializableTypes = serializableTypes;
            ServerMethods = serverMethods;
            ProxyFactory = proxyFactory;
        }

        public Func<string, ContractDescriptor>? DescriptorFactory { get; set; }

        public IReadOnlyList<Type>? SerializableTypes { get; set; }

        public IReadOnlyDictionary<string, GeneratedServerMethodRegistration>? ServerMethods
        {
            get;
            set;
        }

        public Func<IClientInvoker, ContractDescriptorFactory, object>? ProxyFactory { get; set; }

        public bool IsComplete =>
            DescriptorFactory is not null &&
            SerializableTypes is not null &&
            ServerMethods is not null &&
            ProxyFactory is not null;

        public string[] GetMissingComponents()
        {
            var missing = new List<string>(4);
            if (DescriptorFactory is null)
            {
                missing.Add("descriptor");
            }

            if (ProxyFactory is null)
            {
                missing.Add("proxy");
            }

            if (SerializableTypes is null)
            {
                missing.Add("serializer metadata");
            }

            if (ServerMethods is null)
            {
                missing.Add("server dispatch");
            }

            return missing.ToArray();
        }
    }
}

/// <summary>
/// Invokes a generated server operation for a request message.
/// </summary>
/// <param name="target">The target.</param>
/// <param name="request">The request to process.</param>
/// <param name="cancellationToken">The token used to cancel the operation.</param>
/// <returns>A task whose result contains the operation response, or null for one-way operations.</returns>
public delegate ValueTask<object?> GeneratedServerHandler(
    object target,
    object? request,
    CancellationToken cancellationToken);

/// <summary>
/// Invokes a generated server operation that produces a response stream.
/// </summary>
/// <param name="target">The target.</param>
/// <param name="request">The request to process.</param>
/// <param name="cancellationToken">The token used to cancel the operation.</param>
/// <returns>An asynchronous sequence of response items.</returns>
public delegate IAsyncEnumerable<object?> GeneratedServerStreamHandler(
    object target,
    object? request,
    CancellationToken cancellationToken);

/// <summary>
/// Provides the generated server method registration implementation used by AnyProtocol applications.
/// </summary>
public sealed class GeneratedServerMethodRegistration
{
    /// <summary>
    /// Initializes a new instance of the GeneratedServerMethodRegistration class.
    /// </summary>
    /// <param name="methodName">The method name.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    public GeneratedServerMethodRegistration(
        string methodName,
        GeneratedServerHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        MethodName = methodName;
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Initializes a new instance of the GeneratedServerMethodRegistration class.
    /// </summary>
    /// <param name="methodName">The method name.</param>
    /// <param name="streamHandler">The stream handler.</param>
    public GeneratedServerMethodRegistration(
        string methodName,
        GeneratedServerStreamHandler streamHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        MethodName = methodName;
        StreamHandler = streamHandler ??
                        throw new ArgumentNullException(nameof(streamHandler));
    }

    /// <summary>
    /// Gets the method name.
    /// </summary>
    /// <value>The method name.</value>
    public string MethodName { get; }

    /// <summary>
    /// Gets the handler.
    /// </summary>
    /// <value>The handler.</value>
    public GeneratedServerHandler? Handler { get; }

    /// <summary>
    /// Gets the stream handler.
    /// </summary>
    /// <value>The stream handler.</value>
    public GeneratedServerStreamHandler? StreamHandler { get; }
}

/// <summary>
/// Stores and resolves generated server dispatch registrations.
/// </summary>
public static class GeneratedServerDispatchRegistry
{
    /// <summary>
    /// Performs the is registered operation.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="methodName">The method name.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool IsRegistered(Type contractType, string methodName)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return GeneratedContractRegistry.IsServerMethodRegistered(contractType, methodName);
    }

    internal static bool TryGetHandler(
        Type contractType,
        string methodName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out GeneratedServerHandler? handler)
        => GeneratedContractRegistry.TryGetServerHandler(contractType, methodName, out handler);

    internal static bool TryGetStreamHandler(
        Type contractType,
        string methodName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out GeneratedServerStreamHandler? handler)
        => GeneratedContractRegistry.TryGetServerStreamHandler(
            contractType,
            methodName,
            out handler);
}
