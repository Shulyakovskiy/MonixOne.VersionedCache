namespace MonixOne.VersionedCache.Models;

/// <summary>
/// The outcome of an atomic versioned write, including the version retained in Redis.
/// </summary>
public sealed record CacheWriteResult(CacheWriteStatus Status, long CurrentVersion);
