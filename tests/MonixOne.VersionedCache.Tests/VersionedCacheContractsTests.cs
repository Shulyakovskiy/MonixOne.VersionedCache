using Microsoft.Extensions.DependencyInjection;
using MonixOne.VersionedCache.Abstractions;
using MonixOne.VersionedCache.DependencyInjection;
using MonixOne.VersionedCache.Models;
using MonixOne.VersionedCache.Redis;
using Xunit;

namespace MonixOne.VersionedCache.Tests;

public sealed class VersionedCacheContractsTests
{
    [Fact]
    public void CacheWriteResult_PreservesStructuredOutcome()
    {
        var result = new CacheWriteResult(CacheWriteStatus.IgnoredOlderVersion, 42);

        Assert.Equal(CacheWriteStatus.IgnoredOlderVersion, result.Status);
        Assert.Equal(42, result.CurrentVersion);
    }

    [Fact]
    public void VersionedCacheEntry_RepresentsTombstoneWithoutPayload()
    {
        var entry = new VersionedCacheEntry<string>(43, true, null);

        Assert.True(entry.IsDeleted);
        Assert.Equal(43, entry.Version);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void AddVersionedCache_RegistersSingletonImplementation()
    {
        var services = new ServiceCollection();

        services.AddVersionedCache();

        var registration = Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IVersionedCache));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.Equal(typeof(RedisVersionedCache), registration.ImplementationType);
    }
}
