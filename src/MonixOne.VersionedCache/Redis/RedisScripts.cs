using StackExchange.Redis;

namespace MonixOne.VersionedCache.Redis;

internal static class RedisScripts
{
    // Versions are decimal strings. Lua numbers are IEEE 754 doubles and cannot compare all Int64 values safely.
    internal static readonly LuaScript SetIfNewer = LuaScript.Prepare("""
        local function normalize_version(value)
            local normalized = string.gsub(value, '^0+', '')
            if normalized == '' then
                return '0'
            end

            return normalized
        end

        local function compare_versions(left, right)
            left = normalize_version(left)
            right = normalize_version(right)

            if string.len(left) < string.len(right) then
                return -1
            end

            if string.len(left) > string.len(right) then
                return 1
            end

            if left < right then
                return -1
            end

            if left > right then
                return 1
            end

            return 0
        end

        local current = redis.call('HGET', @key, 'v')
        local incoming = normalize_version(@version)

        if not current then
            redis.call('HSET', @key, 'v', incoming, 'd', @deleted, 'p', @payload)
            redis.call('PEXPIRE', @key, @ttlMilliseconds)
            return { '0', incoming }
        end

        local comparison = compare_versions(incoming, current)
        if comparison == 0 then
            return { '1', normalize_version(current) }
        end

        if comparison < 0 then
            return { '2', normalize_version(current) }
        end

        redis.call('HSET', @key, 'v', incoming, 'd', @deleted, 'p', @payload)
        redis.call('PEXPIRE', @key, @ttlMilliseconds)
        return { '0', incoming }
        """);
}
