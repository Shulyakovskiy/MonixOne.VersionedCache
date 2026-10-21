using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MonixOne.VersionedCache;

/// <summary>
/// Registers version-aware Redis cache services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IVersionedCache"/>. The application must separately
    /// register a singleton <c>IConnectionMultiplexer</c>. The environment name is
    /// initialized from the host's <see cref="IHostEnvironment"/> registration and
    /// the service name from <c>Service:Name</c> in <see cref="IConfiguration"/>.
    /// </summary>
    public static IServiceCollection AddVersionedCache(
        this IServiceCollection services,
        Action<VersionedCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<VersionedCacheOptions>()
            .Configure<IHostEnvironment, IConfiguration>(static (options, environment, configuration) =>
            {
                options.EnvironmentName = environment.EnvironmentName;
                options.ServiceName = configuration["Service:Name"] ?? string.Empty;
            })
            .Validate(static options => options.HasValidEnvironmentName,
                VersionedCacheOptions.InvalidEnvironmentNameMessage)
            .Validate(static options => options.HasValidServiceName,
                VersionedCacheOptions.InvalidServiceNameMessage)
            .ValidateOnStart();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IVersionedCache, RedisVersionedCache>();
        return services;
    }
}
