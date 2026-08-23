using AnyProtocol.Logging.Abstraction;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AnyProtocol.Logging.Microsoft.Tests;

public sealed class MicrosoftLogWriterFactoryTests
{
    [Fact]
    public void Constructor_rejects_null_logger_factory()
    {
        Assert.Throws<ArgumentNullException>(() => new MicrosoftLogWriterFactory(null!));
    }

    [Fact]
    public void CreateLogWriter_uses_requested_category_and_returns_writer()
    {
        var logger = new RecordingLogger();
        var loggerFactory = new RecordingLoggerFactory(logger);
        using var factory = new MicrosoftLogWriterFactory(loggerFactory);

        var writer = factory.CreateLogWriter("Orders");
        writer.Log(LogSeverity.Info, "created", null);

        Assert.Equal("Orders", loggerFactory.CategoryName);
        Assert.Single(logger.Entries);
        Assert.IsType<MicrosoftLogWriter>(writer);
    }

    [Fact]
    public void Dispose_forwards_disposal_to_logger_factory()
    {
        var loggerFactory = new RecordingLoggerFactory(new RecordingLogger());
        var factory = new MicrosoftLogWriterFactory(loggerFactory);

        factory.Dispose();

        Assert.True(loggerFactory.IsDisposed);
    }

    private sealed class RecordingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public string? CategoryName { get; private set; }

        public bool IsDisposed { get; private set; }

        public ILogger CreateLogger(string categoryName)
        {
            CategoryName = categoryName;
            return logger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
