using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

public sealed class LoadScenarioRunnerTests
{
    [Theory]
    [InlineData("event")]
    [InlineData("request-reply")]
    [InlineData("competing-consumers")]
    public async Task InMemory_scenarios_complete_without_errors(string scenario)
    {
        var options = new LoadTestOptions
        {
            Transport = "inmemory",
            Scenario = scenario,
            PayloadBytes = 256,
            Concurrency = 4,
            Operations = 50,
            WarmupOperations = 5,
            JsonPath = "unused.json",
            MarkdownPath = "unused.md"
        };

        var result = await new LoadScenarioRunner().RunAsync(options, TransportFactories.Create(options));

        Assert.Equal(50, result.Operations);
        Assert.Equal(0, result.Errors);
        Assert.Equal(0, result.Timeouts);
        Assert.True(result.OperationsPerSecond > 0);
    }
}
