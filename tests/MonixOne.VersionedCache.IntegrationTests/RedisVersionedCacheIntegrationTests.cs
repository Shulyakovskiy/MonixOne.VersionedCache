using Microsoft.Extensions.Options;
using MonixOne.VersionedCache.Configuration;
using MonixOne.VersionedCache.Exceptions;
using MonixOne.VersionedCache.Models;
using MonixOne.VersionedCache.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace MonixOne.VersionedCache.IntegrationTests;

[CollectionDefinition(nameof(RedisCollection), DisableParallelization = true)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;

[Collection(nameof(RedisCollection))]
public sealed class RedisVersionedCacheIntegrationTests(RedisFixture fixture)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task SetIfNewerAsync_WritesAndReadsNormalEntry()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var value = new TestValue("Привет, cache");

        var result = await cache.SetIfNewerAsync(key, 10, value, Ttl, CancellationToken.None);
        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);

        Assert.Equal(new CacheWriteResult(CacheWriteStatus.Written, 10), result);
        AssertEntry(entry, 10, false, "Привет, cache");
    }

    [Fact]
    public async Task GetManyAsync_ReturnsEntriesMissesAndTombstonesByKey()
    {
        var cache = fixture.CreateCache();
        var activeKey = NewKey();
        var missingKey = NewKey();
        var deletedKey = NewKey();
        await cache.SetIfNewerAsync(activeKey, 10, new TestValue("active"), Ttl, CancellationToken.None);
        await cache.SetTombstoneIfNewerAsync(deletedKey, 11, Ttl, CancellationToken.None);

        var entries = await cache.GetManyAsync<TestValue>(
            [activeKey, missingKey, deletedKey, activeKey], CancellationToken.None);

        Assert.Equal(3, entries.Count);
        AssertEntry(entries[activeKey], 10, false, "active");
        Assert.Null(entries[missingKey]);
        AssertEntry(entries[deletedKey], 11, true, null);
    }

    [Fact]
    public async Task GetManyAsync_ReadsKeysAcrossBatchBoundary()
    {
        var cache = fixture.CreateCache();
        var keys = Enumerable.Range(0, 260).Select(static _ => NewKey()).ToArray();
        await cache.SetIfNewerAsync(keys[255], 10, new TestValue("first batch"), Ttl, CancellationToken.None);
        await cache.SetIfNewerAsync(keys[256], 11, new TestValue("second batch"), Ttl, CancellationToken.None);

        var entries = await cache.GetManyAsync<TestValue>(keys, CancellationToken.None);

        Assert.Equal(keys.Length, entries.Count);
        Assert.Null(entries[keys[0]]);
        AssertEntry(entries[keys[255]], 10, false, "first batch");
        AssertEntry(entries[keys[256]], 11, false, "second batch");
        Assert.Null(entries[keys[^1]]);
    }

    [Fact]
    public async Task GetManyAsync_RejectsInvalidKeysBeforeReading()
    {
        var cache = fixture.CreateCache();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => cache.GetManyAsync<TestValue>(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.GetManyAsync<TestValue>([NewKey(), " "], CancellationToken.None));
        Assert.Empty(await cache.GetManyAsync<TestValue>([], CancellationToken.None));
    }

    [Fact]
    public async Task GetManyAsync_PropagatesCorruptedEntry()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        await fixture.Connection.GetDatabase().HashSetAsync(key, "other", "value");

        var exception = await Assert.ThrowsAsync<VersionedCacheCorruptedEntryException>(
            () => cache.GetManyAsync<TestValue>([NewKey(), key], CancellationToken.None));

        Assert.Equal(key, exception.Key);
    }

    [Fact]
    public async Task SetIfNewerAsync_RejectsDuplicateAndOlderWritesWithoutReplacingPayload()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        await cache.SetIfNewerAsync(key, 10, new TestValue("v10"), Ttl, CancellationToken.None);

        var duplicate = await cache.SetIfNewerAsync(key, 10, new TestValue("duplicate"), Ttl, CancellationToken.None);
        var older = await cache.SetIfNewerAsync(key, 9, new TestValue("older"), Ttl, CancellationToken.None);
        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);

        Assert.Equal(new CacheWriteResult(CacheWriteStatus.IgnoredSameVersion, 10), duplicate);
        Assert.Equal(new CacheWriteResult(CacheWriteStatus.IgnoredOlderVersion, 10), older);
        AssertEntry(entry, 10, false, "v10");
    }

    [Fact]
    public async Task Serialization_RoundTripsPrimitiveAndNestedValues()
    {
        var cache = fixture.CreateCache();
        var timestamp = new DateTimeOffset(2026, 9, 22, 12, 30, 0, TimeSpan.FromHours(3));

        await AssertRoundTripAsync(cache, "значение с Unicode");
        await AssertRoundTripAsync(cache, 42);
        await AssertRoundTripAsync(cache, Guid.Parse("019d2af8-cac1-7a0e-a1fb-6e0ff1646530"));
        await AssertRoundTripAsync(cache, timestamp);
        await AssertRoundTripAsync(cache, new NestedValue("profile", null, new ChildValue("nested", timestamp)));
    }

    [Fact]
    public async Task Tombstone_PreventsDelayedOlderEntityFromResurrection()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        await cache.SetIfNewerAsync(key, 10, new TestValue("active"), Ttl, CancellationToken.None);

        var tombstone = await cache.SetTombstoneIfNewerAsync(key, 11, Ttl, CancellationToken.None);
        var stale = await cache.SetIfNewerAsync(key, 10, new TestValue("late"), Ttl, CancellationToken.None);
        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);

        Assert.Equal(new CacheWriteResult(CacheWriteStatus.Written, 11), tombstone);
        Assert.Equal(new CacheWriteResult(CacheWriteStatus.IgnoredOlderVersion, 11), stale);
        AssertEntry(entry, 11, true, null);
    }

    [Fact]
    public async Task GetAsync_ThrowsForCorruptedExistingHash()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        await fixture.Connection.GetDatabase().HashSetAsync(key, "d", "0");

        var exception = await Assert.ThrowsAsync<VersionedCacheCorruptedEntryException>(
            () => cache.GetAsync<TestValue>(key, CancellationToken.None));

        Assert.Equal(key, exception.Key);
        Assert.DoesNotContain("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetIfNewerAsync_ValidatesCallerInput()
    {
        var cache = fixture.CreateCache();

        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.SetIfNewerAsync(" ", 1, new TestValue("value"), Ttl, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.SetIfNewerAsync(NewKey(), 0, new TestValue("value"), Ttl, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.SetIfNewerAsync(NewKey(), 1, new TestValue("value"), TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => cache.SetIfNewerAsync<string>(NewKey(), 1, null!, Ttl, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateAndOlderVersions_DoNotRefreshTtl()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var ttl = TimeSpan.FromSeconds(10);
        await cache.SetIfNewerAsync(key, 10, new TestValue("v10"), ttl, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None);
        var ttlBeforeDuplicate = await fixture.GetPreciseTtlAsync(key, CancellationToken.None);

        await cache.SetIfNewerAsync(key, 10, new TestValue("duplicate"), ttl, CancellationToken.None);
        var ttlAfterDuplicate = await fixture.GetPreciseTtlAsync(key, CancellationToken.None);
        await cache.SetIfNewerAsync(key, 9, new TestValue("older"), ttl, CancellationToken.None);
        var ttlAfterOlder = await fixture.GetPreciseTtlAsync(key, CancellationToken.None);

        Assert.True(ttlAfterDuplicate < ttlBeforeDuplicate, "A duplicate write refreshed the TTL.");
        Assert.True(ttlAfterOlder < ttlAfterDuplicate, "An older write refreshed the TTL.");
    }

    [Fact]
    public async Task NewerVersion_RefreshesTtl()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var ttl = TimeSpan.FromSeconds(10);
        await cache.SetIfNewerAsync(key, 10, new TestValue("v10"), ttl, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None);

        await cache.SetIfNewerAsync(key, 11, new TestValue("v11"), ttl, CancellationToken.None);
        var refreshedTtl = await fixture.GetPreciseTtlAsync(key, CancellationToken.None);

        Assert.InRange(refreshedTtl, 8500, 10000);
    }

    [Fact]
    public async Task ConcurrentWrites_KeepMaximumVersionAndItsPayload()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var versions = Enumerable.Range(1, 100).OrderBy(static _ => Random.Shared.Next()).ToArray();

        await Task.WhenAll(versions.Select(version =>
            cache.SetIfNewerAsync(key, version, new TestValue($"v{version}"), Ttl, CancellationToken.None)));

        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);
        AssertEntry(entry, 100, false, "v100");
    }

    [Fact]
    public async Task StressWrites_WithDuplicatesKeepMaximumVersionAndItsPayload()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var versions = Enumerable.Range(1, 500).Concat(Enumerable.Range(1, 500))
            .OrderBy(static _ => Random.Shared.Next())
            .ToArray();

        await Task.WhenAll(versions.Select(version =>
            cache.SetIfNewerAsync(key, version, new TestValue($"v{version}"), Ttl, CancellationToken.None)));

        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);
        AssertEntry(entry, 500, false, "v500");
    }

    [Fact]
    public async Task ConcurrentTombstone_WinsOverAllOlderWrites()
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        var operations = new Task<CacheWriteResult>[]
        {
            cache.SetIfNewerAsync(key, 100, new TestValue("v100"), Ttl, CancellationToken.None),
            cache.SetTombstoneIfNewerAsync(key, 101, Ttl, CancellationToken.None),
            cache.SetIfNewerAsync(key, 99, new TestValue("v99"), Ttl, CancellationToken.None),
            cache.SetIfNewerAsync(key, 98, new TestValue("v98"), Ttl, CancellationToken.None)
        };

        await Task.WhenAll(operations);

        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);
        AssertEntry(entry, 101, true, null);
    }

    [Theory]
    [InlineData(9L, 10L)]
    [InlineData(99L, 100L)]
    [InlineData(9007199254740991L, 9007199254740992L)]
    [InlineData(9007199254740992L, 9007199254740993L)]
    [InlineData(9223372036854775806L, long.MaxValue)]
    public async Task LargeVersions_AreComparedWithoutLuaNumberPrecisionLoss(long lowerVersion, long higherVersion)
    {
        var cache = fixture.CreateCache();
        var key = NewKey();
        await cache.SetIfNewerAsync(key, lowerVersion, new TestValue($"v{lowerVersion}"), Ttl, CancellationToken.None);

        var result = await cache.SetIfNewerAsync(key, higherVersion, new TestValue($"v{higherVersion}"), Ttl, CancellationToken.None);
        var entry = await cache.GetAsync<TestValue>(key, CancellationToken.None);

        Assert.Equal(new CacheWriteResult(CacheWriteStatus.Written, higherVersion), result);
        AssertEntry(entry, higherVersion, false, $"v{higherVersion}");
    }

    private static string NewKey() => $"versioned-cache:test:v1:{{{Guid.NewGuid():N}}}";

    private static async Task AssertRoundTripAsync<T>(RedisVersionedCache cache, T value)
    {
        var key = NewKey();
        await cache.SetIfNewerAsync(key, 1, value, Ttl, CancellationToken.None);
        var entry = await cache.GetAsync<T>(key, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(1, entry.Version);
        Assert.False(entry.IsDeleted);
        Assert.Equal(value, entry.Value);
    }

    private static void AssertEntry(VersionedCacheEntry<TestValue>? entry, long version, bool isDeleted, string? valueName)
    {
        Assert.NotNull(entry);
        Assert.Equal(version, entry.Version);
        Assert.Equal(isDeleted, entry.IsDeleted);
        Assert.Equal(valueName, entry.Value?.Name);
    }

    public sealed record TestValue(string Name);

    public sealed record NestedValue(string Name, string? OptionalValue, ChildValue Child);

    public sealed record ChildValue(string Name, DateTimeOffset ChangedAt);
}

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7.4.1").Build();
    private IConnectionMultiplexer? _connectionMultiplexer;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionMultiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_connectionMultiplexer is not null)
        {
            await _connectionMultiplexer.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public RedisVersionedCache CreateCache() => new(
        _connectionMultiplexer ?? throw new InvalidOperationException("Redis fixture is not initialized."),
        Options.Create(new VersionedCacheOptions()));

    public IConnectionMultiplexer Connection =>
        _connectionMultiplexer ?? throw new InvalidOperationException("Redis fixture is not initialized.");

    public async Task<long> GetPreciseTtlAsync(string key, CancellationToken cancellationToken)
    {
        var result = await (_connectionMultiplexer ?? throw new InvalidOperationException("Redis fixture is not initialized."))
            .GetDatabase()
            .ExecuteAsync("PTTL", key)
            .WaitAsync(cancellationToken);

        return (long)result;
    }
}
