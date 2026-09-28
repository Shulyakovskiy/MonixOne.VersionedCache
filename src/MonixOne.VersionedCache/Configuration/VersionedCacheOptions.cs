using System.Text.Json;

namespace MonixOne.VersionedCache;

/// <summary>
/// Configures serialization used by <see cref="IVersionedCache"/>.
/// </summary>
public sealed class VersionedCacheOptions
{
    /// <summary>
    /// Gets or sets the serializer options reused for every cache operation.
    /// The default uses the web serializer profile.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
}
