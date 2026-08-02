using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.RabbitMq;

/// <summary>
/// Provides extension methods for rabbit mq link builder configuration and registration.
/// </summary>
public static class RabbitMqLinkBuilderExtensions
{
    /// <summary>
    /// Adds rabbit mq support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the add rabbit mq operation.</returns>
    public static LinkBuilder AddRabbitMq(
        this LinkBuilder builder,
        RabbitMqProtocolOptions options,
        string name = "rabbitmq")
        => AddRabbitMq(builder, options, ProtocolKey.Create(name));

    /// <summary>
    /// Adds rabbit mq support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the add rabbit mq operation.</returns>
    public static LinkBuilder AddRabbitMq(
        this LinkBuilder builder,
        RabbitMqProtocolOptions options,
        ProtocolKey protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.AddTransport(protocol, new RabbitMqMessagingProtocol(options));
    }
}
