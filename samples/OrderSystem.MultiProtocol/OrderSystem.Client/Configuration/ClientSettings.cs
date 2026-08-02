using AnyProtocol.Protocol.Abstraction;
using Microsoft.Extensions.Configuration;

namespace OrderSystem.Client.Configuration;

public sealed record ClientSettings(ProtocolKey Protocol, Uri ServerUri, bool ShowError)
{
    public static ClientSettings From(IConfiguration configuration, string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(args);

        var protocol = configuration["OrderSystem:Protocol"] ?? "Rest";
        var showError = false;

        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--show-error", StringComparison.OrdinalIgnoreCase))
            {
                showError = true;
                continue;
            }

            if (string.Equals(args[index], "--protocol", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("The --protocol option requires a value.", nameof(args));
                }

                protocol = args[++index];
            }
        }

        var selectedProtocol = ParseProtocol(protocol);
        var serverUriKey = selectedProtocol == ProtocolKey.Rest
            ? "OrderSystem:RestUri"
            : "OrderSystem:GrpcUri";
        var serverUriValue = configuration[serverUriKey];
        if (!Uri.TryCreate(serverUriValue, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{serverUriKey} must be an absolute HTTP(S) URI.");
        }

        return new ClientSettings(selectedProtocol, serverUri, showError);
    }

    private static ProtocolKey ParseProtocol(string value)
    {
        if (string.Equals(value, "Rest", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolKey.Rest;
        }

        if (string.Equals(value, "Grpc", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolKey.Grpc;
        }

        throw new InvalidOperationException(
            $"Unsupported protocol '{value}'. Supported values: Rest, Grpc.");
    }
}
