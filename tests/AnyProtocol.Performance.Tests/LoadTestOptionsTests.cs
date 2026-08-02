using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

public sealed class LoadTestOptionsTests
{
    [Fact]
    public void Parse_accepts_the_documented_inmemory_smoke_command()
    {
        var options = LoadTestOptions.Parse(
        [
            "--transport", "inmemory",
            "--scenario", "request-reply",
            "--payload-bytes", "256",
            "--concurrency", "1",
            "--operations", "1000",
            "--warmup", "100",
            "--json", "result.json",
            "--markdown", "result.md"
        ]);

        Assert.Equal("inmemory", options.Transport);
        Assert.Equal(1000, options.Operations);
    }

    [Fact]
    public void Validation_identifies_invalid_option_without_credentials()
    {
        var exception = Assert.Throws<ArgumentException>(() => LoadTestOptions.Parse(
        [
            "--transport", "rabbitmq",
            "--scenario", "event",
            "--rabbitmq-uri", "not-a-uri-with-secret",
            "--json", "result.json",
            "--markdown", "result.md"
        ]));

        Assert.Contains("--rabbitmq-uri", exception.Message);
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public void Full_matrix_expands_to_eighty_one_exact_scenarios()
    {
        var options = LoadTestOptions.Parse(
        [
            "--matrix", "full",
            "--rabbitmq-uri", "amqp://localhost/",
            "--kafka-bootstrap", "localhost:9092",
            "--json", "result.json",
            "--markdown", "result.md"
        ]);

        var expanded = options.Expand();

        Assert.Equal(81, expanded.Count);
        Assert.Equal(81, expanded.Select(item => (item.Transport, item.Scenario, item.PayloadBytes, item.Concurrency)).Distinct().Count());
    }

    [Fact]
    public void Smoke_matrix_is_the_documented_single_scenario()
    {
        var options = LoadTestOptions.Parse(
        ["--matrix", "smoke", "--json", "result.json", "--markdown", "result.md"]);

        var scenario = Assert.Single(options.Expand());

        Assert.Equal("inmemory", scenario.Transport);
        Assert.Equal("request-reply", scenario.Scenario);
        Assert.Equal(256, scenario.PayloadBytes);
        Assert.Equal(1, scenario.Concurrency);
    }
}
