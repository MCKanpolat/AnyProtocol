using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnyProtocol.LoadTests;

public static class ResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        PerformanceRunResult result,
        string jsonPath,
        string markdownPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        if (result.SchemaVersion != PerformanceRunResult.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported performance schema version {result.SchemaVersion}.");
        }

        var ordered = result with { Scenarios = Order(result.Scenarios).ToArray() };
        var json = JsonSerializer.Serialize(ordered, JsonOptions) + Environment.NewLine;
        var markdown = CreateMarkdown(ordered);
        await WriteAtomicAsync(jsonPath, json, cancellationToken);
        await WriteAtomicAsync(markdownPath, markdown, cancellationToken);
    }

    private static IEnumerable<ScenarioResult> Order(IEnumerable<ScenarioResult> scenarios)
        => scenarios
            .OrderBy(static item => item.Transport, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Scenario, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PayloadBytes)
            .ThenBy(static item => item.Concurrency);

    private static string CreateMarkdown(PerformanceRunResult result)
    {
        var output = new StringBuilder();
        output.AppendLine("# AnyProtocol performance results");
        output.AppendLine();
        output.AppendLine("| Transport | Scenario | Payload | Concurrency | Ops/s | p50 ms | p95 ms | p99 ms | Errors | Timeouts |");
        output.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in result.Scenarios)
        {
            output.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| {0} | {1} | {2} | {3} | {4:F3} | {5:F3} | {6:F3} | {7:F3} | {8} | {9} |",
                item.Transport,
                item.Scenario,
                item.PayloadBytes,
                item.Concurrency,
                item.OperationsPerSecond,
                item.P50Milliseconds,
                item.P95Milliseconds,
                item.P99Milliseconds,
                item.Errors,
                item.Timeouts));
        }

        return output.ToString();
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
