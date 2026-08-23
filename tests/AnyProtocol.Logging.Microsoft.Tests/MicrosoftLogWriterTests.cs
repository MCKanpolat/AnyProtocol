using AnyProtocol.Logging.Abstraction;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AnyProtocol.Logging.Microsoft.Tests;

public sealed class MicrosoftLogWriterTests
{
    [Theory]
    [InlineData(LogSeverity.Trace, LogLevel.Trace)]
    [InlineData(LogSeverity.Debug, LogLevel.Debug)]
    [InlineData(LogSeverity.Info, LogLevel.Information)]
    [InlineData(LogSeverity.Warning, LogLevel.Warning)]
    [InlineData(LogSeverity.Error, LogLevel.Error)]
    [InlineData(LogSeverity.Fatal, LogLevel.Critical)]
    public void IsEnabled_maps_anyprotocol_severity_to_microsoft_level(
        LogSeverity severity,
        LogLevel expectedLevel)
    {
        var logger = new RecordingLogger();
        var writer = new MicrosoftLogWriter(logger);

        Assert.True(writer.IsEnabled(severity));
        Assert.Equal(expectedLevel, Assert.Single(logger.EnabledChecks));
    }

    [Fact]
    public void IsEnabled_maps_unknown_severity_to_none()
    {
        var logger = new RecordingLogger();
        var writer = new MicrosoftLogWriter(logger);

        Assert.True(writer.IsEnabled((LogSeverity)999));
        Assert.Equal(LogLevel.None, Assert.Single(logger.EnabledChecks));
    }

    [Fact]
    public void Log_forwards_formatted_message_and_exception_when_enabled()
    {
        var logger = new RecordingLogger();
        var writer = new MicrosoftLogWriter(logger);
        var exception = new InvalidOperationException("boom");

        writer.Log(LogSeverity.Warning, "Order {0} failed", exception, 42);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Null(entry.EventId.Name);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("Order 42 failed", entry.Message);
    }

    [Fact]
    public void Log_does_not_call_logger_when_level_is_disabled()
    {
        var logger = new RecordingLogger(LogLevel.Error);
        var writer = new MicrosoftLogWriter(logger);

        writer.Log(LogSeverity.Info, "not written", null);

        Assert.Empty(logger.Entries);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.EnabledChecks));
    }

    [Fact]
    public void LogEvent_forwards_event_id_and_payload()
    {
        var logger = new RecordingLogger();
        var writer = new MicrosoftLogWriter(logger);

        writer.LogEvent(123, LogSeverity.Error, "Failure {0}", null, "request-1");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(123, entry.EventId.Id);
        Assert.Equal("Failure request-1", entry.Message);
    }

    [Fact]
    public void LogEvent_does_not_call_logger_when_level_is_disabled()
    {
        var logger = new RecordingLogger(LogLevel.Critical);
        var writer = new MicrosoftLogWriter(logger);

        writer.LogEvent(123, LogSeverity.Info, "not written", null);

        Assert.Empty(logger.Entries);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.EnabledChecks));
    }

    [Fact]
    public void Constructor_rejects_null_logger()
    {
        Assert.Throws<ArgumentNullException>(() => new MicrosoftLogWriter(null!));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly HashSet<LogLevel> _enabledLevels;

        public RecordingLogger(params LogLevel[] enabledLevels)
        {
            _enabledLevels = enabledLevels.Length == 0
                ? Enum.GetValues<LogLevel>().ToHashSet()
                : enabledLevels.ToHashSet();
        }

        public List<LogLevel> EnabledChecks { get; } = [];

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            EnabledChecks.Add(logLevel);
            return _enabledLevels.Contains(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        string Message);
}
