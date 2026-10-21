using System.Text.Json;

namespace MonixOne.VersionedCache;

/// <summary>
/// Configures environment and service isolation and serialization used by <see cref="IVersionedCache"/>.
/// </summary>
public sealed class VersionedCacheOptions
{
    internal const string InvalidEnvironmentNameMessage =
        "EnvironmentName is required and must not contain whitespace, ':', '{', or '}'.";
    internal const string InvalidServiceNameMessage =
        "Service:Name is required and must not contain whitespace, ':', '{', or '}'.";

    /// <summary>
    /// Gets or sets the required environment component of the Redis key prefix
    /// <c>{environmentName}:{serviceName}:</c>. Registration initializes it from
    /// <c>IHostEnvironment.EnvironmentName</c> before applying custom options.
    /// When constructing the cache directly, set this value explicitly.
    /// The value is case-sensitive and must not contain whitespace, ':', '{', or '}'.
    /// </summary>
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the required service component of the Redis key prefix.
    /// Registration initializes it from <c>Service:Name</c> in <c>IConfiguration</c>
    /// before applying custom options. When constructing the cache directly, set it explicitly.
    /// The value is case-sensitive and must not contain whitespace, ':', '{', or '}'.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serializer options reused for every cache operation.
    /// The default uses the web serializer profile.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    internal bool HasValidEnvironmentName => IsValidPrefixComponent(EnvironmentName);

    internal bool HasValidServiceName => IsValidPrefixComponent(ServiceName);

    private static bool IsValidPrefixComponent(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(static character => char.IsWhiteSpace(character) || character is ':' or '{' or '}');
}
