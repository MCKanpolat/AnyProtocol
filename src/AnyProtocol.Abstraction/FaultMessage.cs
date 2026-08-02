namespace AnyProtocol.Abstraction;

/// <summary>
/// Describes one structured detail associated with a fault.
/// </summary>
/// <param name="Key">The key that identifies the value.</param>
/// <param name="Message">The message payload or description.</param>
public sealed record FaultDetail(string Key, string Message);

/// <summary>
/// Contains a serialized fault returned to a AnyProtocol caller.
/// </summary>
/// <param name="Code">The machine-readable code.</param>
/// <param name="Message">The message payload or description.</param>
/// <param name="ExceptionType">The optional runtime exception type.</param>
/// <param name="Retryable">true if the caller may safely retry the operation; otherwise, false.</param>
/// <param name="Details">Optional structured details that further describe the result.</param>
public sealed record FaultMessage(
    string Code,
    string Message,
    string? ExceptionType = null,
    bool Retryable = false,
    IReadOnlyList<FaultDetail>? Details = null);
