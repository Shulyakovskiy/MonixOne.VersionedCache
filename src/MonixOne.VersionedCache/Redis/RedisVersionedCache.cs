using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MonixOne.VersionedCache.Abstractions;
using MonixOne.VersionedCache.Configuration;
using MonixOne.VersionedCache.Exceptions;
using MonixOne.VersionedCache.Models;
using StackExchange.Redis;

namespace MonixOne.VersionedCache.Redis;

/// <summary>
/// Redis hash implementation of <see cref="IVersionedCache"/>.
/// </summary>
public sealed class RedisVersionedCache : IVersionedCache
{
    private const int MaxConcurrentReads = 256;

    private readonly IDatabase _database;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>
    /// Initializes a stateless cache backed by the application's singleton Redis connection.
    /// </summary>
    public RedisVersionedCache(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<VersionedCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _database = connectionMultiplexer.GetDatabase();
        _serializerOptions = options.Value.SerializerOptions
            ?? throw new ArgumentException("Serializer options must not be null.", nameof(options));
    }

    /// <inheritdoc />
    public async Task<VersionedCacheEntry<T>?> GetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        var fields = await _database.HashGetAllAsync(key).WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        if (fields.Length == 0)
        {
            return null;
        }

        RedisValue versionValue = default;
        RedisValue deletedValue = default;
        RedisValue payloadValue = default;
        foreach (var field in fields)
        {
            if (field.Name == "v")
            {
                versionValue = field.Value;
            }
            else if (field.Name == "d")
            {
                deletedValue = field.Value;
            }
            else if (field.Name == "p")
            {
                payloadValue = field.Value;
            }
        }

        if (versionValue.IsNull || deletedValue.IsNull || payloadValue.IsNull)
        {
            throw new VersionedCacheCorruptedEntryException(key, "one or more required hash fields are missing");
        }

        var versionText = versionValue.ToString();
        if (!long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
        {
            throw new VersionedCacheCorruptedEntryException(key, "field 'v' is not a positive Int64");
        }

        var deleted = deletedValue.ToString();
        return deleted switch
        {
            "1" when payloadValue.IsNullOrEmpty => new VersionedCacheEntry<T>(version, true, default),
            "1" => throw new VersionedCacheCorruptedEntryException(key, "tombstone payload must be empty"),
            "0" when !payloadValue.IsNullOrEmpty => new VersionedCacheEntry<T>(version, false, Deserialize<T>(key, payloadValue)),
            "0" => throw new VersionedCacheCorruptedEntryException(key, "non-deleted entry payload is missing"),
            _ => throw new VersionedCacheCorruptedEntryException(key, "field 'd' must be '0' or '1'")
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, VersionedCacheEntry<T>?>> GetManyAsync<T>(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var distinctKeys = keys.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in distinctKeys)
        {
            ValidateKey(key);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var result = new Dictionary<string, VersionedCacheEntry<T>?>(distinctKeys.Length, StringComparer.Ordinal);
        for (var offset = 0; offset < distinctKeys.Length; offset += MaxConcurrentReads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(MaxConcurrentReads, distinctKeys.Length - offset);
            var reads = new Task<VersionedCacheEntry<T>?>[count];
            for (var index = 0; index < count; index++)
            {
                reads[index] = GetAsync<T>(distinctKeys[offset + index], cancellationToken);
            }

            var entries = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var index = 0; index < count; index++)
            {
                result.Add(distinctKeys[offset + index], entries[index]);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public Task<CacheWriteResult> SetIfNewerAsync<T>(
        string key,
        long version,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        return SetAsync(
            key,
            version,
            isDeleted: false,
            JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions),
            ttl,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CacheWriteResult> SetTombstoneIfNewerAsync(
        string key,
        long version,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) =>
        SetAsync(key, version, isDeleted: true, Array.Empty<byte>(), ttl, cancellationToken);

    private async Task<CacheWriteResult> SetAsync(
        string key,
        long version,
        bool isDeleted,
        byte[] payload,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ValidateVersion(version);
        var ttlMilliseconds = GetTtlMilliseconds(ttl);

        // LuaScript uses StackExchange.Redis' built-in script cache (EVALSHA with automatic loading).
        var result = await _database.ScriptEvaluateAsync(
                RedisScripts.SetIfNewer,
                new
                {
                    key = (RedisKey)key,
                    version = version.ToString(CultureInfo.InvariantCulture),
                    deleted = isDeleted ? "1" : "0",
                    payload = (RedisValue)payload,
                    ttlMilliseconds = ttlMilliseconds.ToString(CultureInfo.InvariantCulture)
                })
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return ParseWriteResult(result);
    }

    private T Deserialize<T>(string key, RedisValue payload)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>((byte[])payload!, _serializerOptions);
            if (value is null)
            {
                throw new VersionedCacheCorruptedEntryException(key, "non-deleted entry payload is null");
            }

            return value;
        }
        catch (JsonException exception)
        {
            throw new VersionedCacheCorruptedEntryException(key, $"payload cannot be deserialized as {typeof(T).Name}: {exception.Message}");
        }
    }

    private static CacheWriteResult ParseWriteResult(RedisResult result)
    {
        if (result.Resp2Type != ResultType.Array)
        {
            throw new InvalidOperationException("Versioned cache Lua script returned an unexpected result.");
        }

        var values = (RedisResult[])result!;
        if (values.Length != 2 ||
            !long.TryParse(values[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var currentVersion) ||
            currentVersion <= 0)
        {
            throw new InvalidOperationException("Versioned cache Lua script returned an invalid version.");
        }

        var status = values[0].ToString() switch
        {
            "0" => CacheWriteStatus.Written,
            "1" => CacheWriteStatus.IgnoredSameVersion,
            "2" => CacheWriteStatus.IgnoredOlderVersion,
            _ => throw new InvalidOperationException("Versioned cache Lua script returned an invalid write status.")
        };

        return new CacheWriteResult(status, currentVersion);
    }

    private static long GetTtlMilliseconds(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "TTL must be greater than zero.");
        }

        return checked((long)Math.Ceiling(ttl.TotalMilliseconds));
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key must not be null, empty, or whitespace.", nameof(key));
        }
    }

    private static void ValidateVersion(long version)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Version must be greater than zero.");
        }
    }
}
