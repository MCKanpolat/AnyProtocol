using AnyProtocol.LoadTests;

namespace AnyProtocol.Performance.Tests;

public sealed class ResultWriterTests
{
    [Fact]
    public async Task WriteAsync_emits_camel_case_json_and_deterministic_markdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"anyprotocol-perf-{Guid.NewGuid():N}");
        var json = Path.Combine(directory, "result.json");
        var markdown = Path.Combine(directory, "result.md");
        var result = PerformanceTestData.Run(
            PerformanceTestData.Scenario("rabbitmq", "event", 4096, 8),
            PerformanceTestData.Scenario("inmemory", "event", 256, 1));

        await ResultWriter.WriteAsync(result, json, markdown);

        var jsonText = await File.ReadAllTextAsync(json);
        var markdownText = await File.ReadAllTextAsync(markdown);
        Assert.Contains("\"schemaVersion\": 1", jsonText);
        Assert.DoesNotContain("SchemaVersion", jsonText);
        Assert.True(jsonText.IndexOf("inmemory", StringComparison.Ordinal) < jsonText.IndexOf("rabbitmq", StringComparison.Ordinal));
        Assert.Contains("| Transport | Scenario | Payload | Concurrency | Ops/s | p50 ms | p95 ms | p99 ms | Errors | Timeouts |", markdownText);
        Assert.True(markdownText.IndexOf("inmemory", StringComparison.Ordinal) < markdownText.IndexOf("rabbitmq", StringComparison.Ordinal));
    }
}
