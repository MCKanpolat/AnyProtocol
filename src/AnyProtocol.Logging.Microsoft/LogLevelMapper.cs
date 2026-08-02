using Microsoft.Extensions.Logging;
using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol.Logging.Microsoft;

internal static class LogLevelMapper
{
    public static LogLevel Map(LogSeverity logSeverity)
    {
        return logSeverity switch
        {
            LogSeverity.Trace => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Fatal => LogLevel.Critical,
            _ => LogLevel.None
        };
    }
}