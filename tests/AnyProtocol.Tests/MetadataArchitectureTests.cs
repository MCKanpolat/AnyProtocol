using Xunit;

namespace AnyProtocol.Tests;

public sealed class MetadataArchitectureTests
{
    [Fact]
    public void Direct_identity_and_clock_calls_are_limited_to_the_reviewed_allowlist()
    {
        var sourceRoot = FindRepositoryRoot();
        var allowedGuidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/AnyProtocol/Services/DefaultMessageIdGenerator.cs",
            "src/AnyProtocol/RequestReplyEngine.cs",
            "src/AnyProtocol/StreamEngine.cs",
            "src/AnyProtocol.Protocol.Kafka/KafkaProtocolOptions.cs",
            "src/AnyProtocol.Protocol.Kafka/KafkaMessagingProtocol.cs",
            "src/AnyProtocol.Protocol.RabbitMq/RabbitMqProtocolOptions.cs",
            "src/AnyProtocol.Protocol.RabbitMq/RabbitMqMessagingProtocol.cs",
            "src/AnyProtocol.Protocol.ZeroMq/ZeroMqMessagingProtocol.cs"
        };
        var allowedClockFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/AnyProtocol/Services/DefaultDateTimeProvider.cs"
        };
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(sourceRoot, "src"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var segments = file.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
                segments.Contains("obj", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var fileText = File.ReadAllText(file);
            if (fileText.Contains("Guid.NewGuid", StringComparison.Ordinal) &&
                !allowedGuidFiles.Contains(relativePath))
            {
                violations.Add($"Guid.NewGuid: {relativePath}");
            }

            if ((fileText.Contains("DateTime.UtcNow", StringComparison.Ordinal) ||
                 fileText.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal)) &&
                !allowedClockFiles.Contains(relativePath))
            {
                violations.Add($"UtcNow: {relativePath}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Direct metadata calls escaped the reviewed allowlist:\n" +
            string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AnyProtocol.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("Could not locate the repository root.");
    }
}
