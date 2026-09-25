using Microsoft.Extensions.DependencyInjection;
using MonixOne.VersionedCache.Abstractions;
using MonixOne.VersionedCache.Configuration;
using MonixOne.VersionedCache.Redis;

namespace MonixOne.VersionedCache.DependencyInjection;

/// <summary>
/// Registers version-aware Redis cache services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IVersionedCache"/>. The application must separately
    /// register a singleton <c>IConnectionMultiplexer</c>.
    /// </summary>
    public static IServiceCollection AddVersionedCache(
        this IServiceCollection services,
        Action<VersionedCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<VersionedCacheOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IVersionedCache, RedisVersionedCache>();
        return services;
    }
}
