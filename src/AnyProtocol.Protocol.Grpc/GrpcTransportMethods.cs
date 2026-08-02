using Grpc.Core;

namespace AnyProtocol.Protocol.Grpc;

/// <summary>
/// Provides the grpc transport methods implementation used by AnyProtocol applications.
/// </summary>
public static class GrpcTransportMethods
{
    private static readonly Marshaller<byte[]> IdentityMarshaller =
        Marshallers.Create(static value => value, static value => value);

    /// <summary>
    /// Gets the unary.
    /// </summary>
    /// <value>The unary.</value>
    public static Method<byte[], byte[]> Unary { get; } = new(
        MethodType.Unary,
        "anyprotocol.Transport",
        "Unary",
        IdentityMarshaller,
        IdentityMarshaller);

    /// <summary>
    /// Gets the server stream.
    /// </summary>
    /// <value>The server stream.</value>
    public static Method<byte[], byte[]> ServerStream { get; } = new(
        MethodType.ServerStreaming,
        "anyprotocol.Transport",
        "ServerStream",
        IdentityMarshaller,
        IdentityMarshaller);
}
