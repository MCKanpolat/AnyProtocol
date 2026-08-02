using System.Runtime.InteropServices;
using System.Text.Json;

namespace AnyProtocol.LoadTests;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = LoadTestOptions.Parse(args);
            var runner = new LoadScenarioRunner();
            var configurations = options.Expand();
            var scenarios = new List<ScenarioResult>(configurations.Count);
            foreach (var configuration in configurations)
            {
                scenarios.Add(await runner.RunAsync(configuration, TransportFactories.Create(configuration)));
            }

            var run = new PerformanceRunResult(
                PerformanceRunResult.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                CreateRuntimeMetadata(),
                scenarios);
            await ResultWriter.WriteAsync(run, options.JsonPath, options.MarkdownPath);
            if (scenarios.Any(static result => result.Errors != 0 || result.Timeouts != 0))
            {
                return 3;
            }

            if (options.BaselinePath is not null)
            {
                var baseline = await ReadResultAsync(options.BaselinePath);
                var comparison = BaselineComparer.Compare(baseline, run, options.Threshold);
                if (comparison.Regressions.Count != 0 && options.ConfirmRegressions)
                {
                    var regressedKeys = comparison.Regressions
                        .Select(static item => Key(item.Candidate))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var confirmationScenarios = new List<ScenarioResult>();
                    foreach (var configuration in configurations.Where(item => regressedKeys.Contains(Key(item))))
                    {
                        confirmationScenarios.Add(
                            await runner.RunAsync(configuration, TransportFactories.Create(configuration)));
                    }

                    var confirmation = run with { Scenarios = confirmationScenarios };
                    if (BaselineComparer.Compare(baseline, confirmation, options.Threshold).Regressions.Count != 0)
                    {
                        return 4;
                    }
                }
            }

            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Load test failed: {exception.GetType().Name}.");
            return 3;
        }
    }

    private static async Task<PerformanceRunResult> ReadResultAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PerformanceRunResult>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("The performance baseline is empty.");
    }

    private static string Key(LoadTestOptions item)
        => $"{item.Transport}\u001f{item.Scenario}\u001f{item.PayloadBytes}\u001f{item.Concurrency}";

    private static string Key(ScenarioResult item)
        => $"{item.Transport}\u001f{item.Scenario}\u001f{item.PayloadBytes}\u001f{item.Concurrency}";

    private static RuntimeMetadata CreateRuntimeMetadata()
        => new(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount);
}
