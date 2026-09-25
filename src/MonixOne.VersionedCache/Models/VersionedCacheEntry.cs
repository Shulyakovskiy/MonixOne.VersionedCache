namespace MonixOne.VersionedCache.Models;

/// <summary>
/// A cached projection together with the entity version used to create it.
/// </summary>
public sealed record VersionedCacheEntry<T>(long Version, bool IsDeleted, T? Value);
