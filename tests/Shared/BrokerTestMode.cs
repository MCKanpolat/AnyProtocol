namespace AnyProtocol.Tests.Shared;

internal static class BrokerTestMode
{
    internal const string RequireBrokerTestsEnvironmentVariable = "ANYPROTOCOL_REQUIRE_BROKER_TESTS";

    internal static bool RequireBrokerTests()
        => RequireBrokerTests(Environment.GetEnvironmentVariable(RequireBrokerTestsEnvironmentVariable));

    internal static bool RequireBrokerTests(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "0" or "false" => false,
            "1" or "true" => true,
            _ => throw new InvalidOperationException(
                $"{RequireBrokerTestsEnvironmentVariable} must be 'true' or 'false'.")
        };
}
