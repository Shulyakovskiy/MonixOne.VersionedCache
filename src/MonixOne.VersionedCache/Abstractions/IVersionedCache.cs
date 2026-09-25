using MonixOne.VersionedCache.Models;

namespace MonixOne.VersionedCache.Abstractions;

/// <summary>
/// Stores cache projections so an older entity version cannot overwrite a newer one.
/// Cache keys are supplied by the caller and Redis is never treated as a source of truth.
/// </summary>
public interface IVersionedCache
{
    /// <summary>
    /// Gets one cached projection or tombstone in a single Redis operation.
    /// </summary>
    Task<VersionedCacheEntry<T>?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="value"/> only when <paramref name="version"/> is newer than the
    /// version currently stored for <paramref name="key"/>. Duplicate and stale writes do not
    /// change the payload or refresh the TTL.
    /// </summary>
    Task<CacheWriteResult> SetIfNewerAsync<T>(
        string key,
        long version,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a versioned deletion marker only when <paramref name="version"/> is newer. The
    /// tombstone prevents delayed older messages from resurrecting an entity until its TTL expires.
    /// </summary>
    Task<CacheWriteResult> SetTombstoneIfNewerAsync(
        string key,
        long version,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}
