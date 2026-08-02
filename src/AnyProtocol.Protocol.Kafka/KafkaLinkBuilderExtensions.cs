using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.Kafka;

/// <summary>
/// Provides extension methods for kafka link builder configuration and registration.
/// </summary>
public static class KafkaLinkBuilderExtensions
{
    /// <summary>
    /// Adds kafka support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the add kafka operation.</returns>
    public static LinkBuilder AddKafka(
        this LinkBuilder builder,
        KafkaProtocolOptions options,
        string name = "kafka")
        => AddKafka(builder, options, ProtocolKey.Create(name));

    /// <summary>
    /// Adds kafka support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the add kafka operation.</returns>
    public static LinkBuilder AddKafka(
        this LinkBuilder builder,
        KafkaProtocolOptions options,
        ProtocolKey protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.AddTransport(protocol, new KafkaMessagingProtocol(options));
    }
}
