namespace AnyProtocol.Storage.Abstraction;

/// <summary>Provider-neutral metadata carried by a stored-payload envelope.</summary>
public sealed record StoredPayloadReference(
    string StoreName,
    string Key,
    long Length,
    string Sha256,
    DateTimeOffset ExpiresAt);

/// <summary>Metadata and retention required when writing a serialized body.</summary>
public sealed record PayloadWriteOptions(
    TimeSpan TimeToLive,
    long ExpectedLength,
    string Sha256);
