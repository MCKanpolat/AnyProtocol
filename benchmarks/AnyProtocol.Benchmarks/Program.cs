using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace AnyProtocol.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddExporter(JsonExporter.Full)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .WithArtifactsPath("artifacts/benchmarks");
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
