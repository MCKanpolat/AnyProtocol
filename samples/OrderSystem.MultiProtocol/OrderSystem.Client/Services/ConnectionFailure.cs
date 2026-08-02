using Grpc.Core;

namespace OrderSystem.Client.Services;

public static class ConnectionFailure
{
    public static bool IsConnectionLevel(Exception exception)
        => exception is HttpRequestException or IOException or
           RpcException { StatusCode: StatusCode.Unavailable };
}
