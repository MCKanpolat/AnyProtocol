using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnyProtocol.DependencyInjection.Microsoft;

/// <summary>
/// Provides the anyprotocol health checks implementation used by AnyProtocol applications.
/// </summary>
public static class AnyProtocolHealthChecks
{
    /// <summary>
    /// The liveness name value.
    /// </summary>
    public const string LivenessName = "anyprotocol_liveness";
    /// <summary>
    /// The readiness name value.
    /// </summary>
    public const string ReadinessName = "anyprotocol_readiness";
    /// <summary>
    /// The live tag value.
    /// </summary>
    public const string LiveTag = "live";
    /// <summary>
    /// The ready tag value.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Registers AnyProtocol liveness and readiness checks. Select the checks by the
    /// <see cref="LiveTag"/> and <see cref="ReadyTag"/> tags when mapping endpoints.
    /// </summary>
    public static IHealthChecksBuilder AddAnyProtocolHealthChecks(
        this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddCheck<AnyProtocolLivenessCheck>(
                LivenessName,
                failureStatus: HealthStatus.Unhealthy,
                tags: [LiveTag])
            .AddCheck<AnyProtocolReadinessCheck>(
                ReadinessName,
                failureStatus: HealthStatus.Unhealthy,
                tags: [ReadyTag]);
    }
}

internal sealed class AnyProtocolLivenessCheck(IAnyProtocolBus bus) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            bus.State == AnyProtocolBusState.Disposed
                ? HealthCheckResult.Unhealthy("The AnyProtocol bus is disposed.")
                : HealthCheckResult.Healthy($"The AnyProtocol bus is {bus.State}."));
    }
}

internal sealed class AnyProtocolReadinessCheck(
    IAnyProtocolBus bus,
    TransportRegistry transports) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (bus.State != AnyProtocolBusState.Started)
        {
            return HealthCheckResult.Unhealthy($"The AnyProtocol bus is {bus.State}.");
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, transport) in transports.Entries)
        {
            var protocolName = name.Value;
            if (transport is not ITransportReadiness contributor)
            {
                data[protocolName] = "not-assessed";
                continue;
            }

            TransportReadinessResult readiness;
            try
            {
                readiness = await contributor.CheckReadinessAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                data[protocolName] = exception.GetType().Name;
                return HealthCheckResult.Unhealthy(
                    $"Transport '{name}' readiness check failed.",
                    data: data);
            }

            data[protocolName] = readiness.State.ToString();
            if (readiness.State == TransportReadinessState.NotReady)
            {
                return HealthCheckResult.Unhealthy(
                    $"Transport '{name}' is not ready: {readiness.Description}",
                    data: data);
            }
        }

        return HealthCheckResult.Healthy("AnyProtocol is ready.", data);
    }
}
