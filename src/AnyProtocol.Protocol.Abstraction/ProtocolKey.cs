namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Identifies a configured protocol instance. Values are normalized so keys are
/// case-insensitive while still allowing custom and multiple protocol instances.
/// </summary>
public readonly record struct ProtocolKey
{
    private ProtocolKey(string value) => Value = value;

    /// <summary>
    /// Gets the value represented by this member.
    /// </summary>
    /// <value>The value.</value>
    public string Value { get; } = string.Empty;

    /// <summary>
    /// Gets the default.
    /// </summary>
    /// <value>The default.</value>
    public static ProtocolKey Default { get; } = new("default");

    /// <summary>
    /// Gets the rest.
    /// </summary>
    /// <value>The rest.</value>
    public static ProtocolKey Rest { get; } = new("rest");

    /// <summary>
    /// Gets the grpc.
    /// </summary>
    /// <value>The grpc.</value>
    public static ProtocolKey Grpc { get; } = new("grpc");

    /// <summary>
    /// Gets the mcp.
    /// </summary>
    /// <value>The mcp.</value>
    public static ProtocolKey Mcp { get; } = new("mcp");

    /// <summary>
    /// Gets the kafka.
    /// </summary>
    /// <value>The kafka.</value>
    public static ProtocolKey Kafka { get; } = new("kafka");

    /// <summary>
    /// Gets the rabbit mq.
    /// </summary>
    /// <value>The rabbit mq.</value>
    public static ProtocolKey RabbitMq { get; } = new("rabbitmq");

    /// <summary>
    /// Gets the zero mq.
    /// </summary>
    /// <value>The zero mq.</value>
    public static ProtocolKey ZeroMq { get; } = new("zeromq");

    /// <summary>
    /// Gets the in memory.
    /// </summary>
    /// <value>The in memory.</value>
    public static ProtocolKey InMemory { get; } = new("inmemory");

    /// <summary>
    /// Creates .
    /// </summary>
    /// <param name="value">The value to store.</param>
    /// <returns>The result of the create operation.</returns>
    public static ProtocolKey Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ProtocolKey(value.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Performs the to string operation.
    /// </summary>
    /// <returns>The result of the to string operation.</returns>
    public override string ToString() => Value;
}
