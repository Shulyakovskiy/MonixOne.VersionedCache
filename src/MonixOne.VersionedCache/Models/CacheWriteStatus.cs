namespace MonixOne.VersionedCache.Models;

/// <summary>
/// Describes whether an attempted versioned cache write changed Redis.
/// </summary>
public enum CacheWriteStatus
{
    /// <summary>The incoming version was newer and was stored.</summary>
    Written,

    /// <summary>The incoming version matched the stored version and was ignored.</summary>
    IgnoredSameVersion,

    /// <summary>The incoming version was older than the stored version and was ignored.</summary>
    IgnoredOlderVersion
}
