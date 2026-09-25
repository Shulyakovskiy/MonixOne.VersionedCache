namespace MonixOne.VersionedCache.Exceptions;

/// <summary>
/// Thrown when an existing Redis hash does not satisfy the versioned-cache representation contract.
/// </summary>
public sealed class VersionedCacheCorruptedEntryException : InvalidOperationException
{
    /// <summary>Initializes the exception for the corrupted cache key.</summary>
    public VersionedCacheCorruptedEntryException(string key, string reason)
        : base($"Versioned cache entry for key '{key}' is corrupted: {reason}")
    {
        Key = key;
    }

    /// <summary>The cache key whose representation is invalid.</summary>
    public string Key { get; }
}
