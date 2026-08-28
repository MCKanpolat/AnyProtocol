namespace AnyProtocol.Logging.Abstraction;

/// <summary>Controls how exception details may cross the default logging boundary.</summary>
public enum ExceptionLoggingMode
{
    /// <summary>Emits only a stable exception type name.</summary>
    TypeOnly,
    /// <summary>Emits a generic summary and a stable exception type name.</summary>
    SanitizedSummary,
    /// <summary>Emits the original exception for an explicitly controlled diagnostic sink.</summary>
    FullDiagnostic
}

/// <summary>Produces logging-safe exception metadata without retaining exception content.</summary>
public interface IExceptionSanitizer
{
    /// <summary>Creates safe exception metadata for the selected logging mode.</summary>
    SanitizedException Sanitize(Exception exception, ExceptionLoggingMode mode);
}

/// <summary>Contains exception metadata safe to include in a default log message.</summary>
public sealed record SanitizedException(string Type, string? Summary);

/// <summary>Default exception sanitizer that never copies exception messages, data, or stack traces.</summary>
public sealed class DefaultExceptionSanitizer : IExceptionSanitizer
{
    /// <inheritdoc />
    public SanitizedException Sanitize(Exception exception, ExceptionLoggingMode mode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SanitizedException(
            exception.GetType().Name,
            mode == ExceptionLoggingMode.SanitizedSummary ? "The operation failed." : null);
    }
}
