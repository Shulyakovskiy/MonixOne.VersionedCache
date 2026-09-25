# MonixOne.VersionedCache

`MonixOne.VersionedCache` — небольшой NuGet-пакет для атомарного хранения
версионированных Redis read-model. Он гарантирует, что запись со старой версией не
перезапишет более новую запись независимо от порядка доставки, duplicate delivery и
количества реплик.

Redis остаётся disposable cache. PostgreSQL (или другая основная БД) — source of truth.

## Установка и регистрация

Приложение само создаёт и регистрирует один `IConnectionMultiplexer`; пакет не открывает
соединения на каждый запрос.

```csharp
services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(configuration.GetConnectionString("Redis")!));

services.AddVersionedCache(options =>
{
    options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
});
```

`IVersionedCache` и `RedisVersionedCache` имеют singleton lifetime и не хранят
request-specific state.

## Использование

```csharp
var key = $"beetroute-profile:profile:v1:{{{profileId}}}";

var result = await versionedCache.SetIfNewerAsync(
    key,
    version: profile.Version,
    value: new ProfileCacheModel(profile.Id, profile.DisplayName),
    ttl: TimeSpan.FromMinutes(30),
    cancellationToken);

if (result.Status == CacheWriteStatus.Written)
{
    // Обновлена cache projection.
}
```

Формат ключа рекомендуемый, но не навязывается библиотекой:

```text
{namespace}:{projection}:v{schemaVersion}:{entityId}
```

`v1` в ключе — версия схемы cache model. Поле Redis `v` — монотонная версия конкретной
сущности. Для Redis Cluster полезно поместить `entityId` в hash tag: `{entityId}`.
Не включайте PII в cache key.

## Удаление

Удаление — это tombstone, а не `DEL`: он сохраняет версию и не позволяет запоздалому
событию воскресить удалённую сущность.

```csharp
await versionedCache.SetTombstoneIfNewerAsync(
    key,
    version: deletedProfile.Version,
    ttl: TimeSpan.FromDays(1),
    cancellationToken);
```

`GetAsync<T>` возвращает `null` для отсутствующего ключа и
`VersionedCacheEntry<T> { IsDeleted = true, Value = null }` для tombstone.

Для чтения набора известных ключей используйте `GetManyAsync<T>`:

```csharp
var entries = await versionedCache.GetManyAsync<ProfileCacheModel>(
    profileKeys,
    cancellationToken);
```

Результат содержит каждый уникальный ключ. Отсутствующий ключ соответствует `null`,
tombstone остаётся `VersionedCacheEntry<T>` с `IsDeleted = true`. Чтения выполняются
отдельными Redis `HGETALL` порциями до 256 ключей. Результат не является согласованным
снимком нескольких ключей; перечисления ключей по префиксу нет.

## Гарантии записи

Запись хранится как Redis Hash с полями `v` (version), `d` (deleted) и `p` (UTF-8 JSON
payload). Один Lua script атомарно сравнивает версию, записывает hash и устанавливает TTL.
Нет небезопасной последовательности `GET → сравнение в C# → SET` и нет локальных lock.

Lua не преобразует версию через `tonumber`: `long` передаётся decimal string и сравнивается
по длине и лексикографически. Поэтому корректно поддерживаются значения вплоть до
`long.MaxValue`, включая значения выше IEEE-754 safe integer range.

TTL меняется только при `Written`. Повторная или устаревшая запись не изменяет payload и
не продлевает жизнь ключа.

## PGMQ и транзакция PostgreSQL

Пакет не зависит от PGMQ, EF Core и outbox. Если приложение использует PGMQ в той же
PostgreSQL базе, оно публикует сообщение PGMQ в явной транзакции вместе с domain change:

```text
PostgreSQL domain change + pgmq.send
             │  один явный DbTransaction
             ▼
          commit
             ▼
PGMQ consumer (at-least-once) → IVersionedCache → Redis
```

Отдельный transactional outbox для этого контура не нужен. At-least-once delivery всё равно
учитывается: duplicate и out-of-order сообщения безопасны благодаря версионному Lua-CAS.

## Метрики и ошибки

Вызывающий код может строить метрику по `CacheWriteResult.Status`:

```text
versioned_cache_write_total{result="written|same_version|older_version"}
versioned_cache_read_total{result="hit|miss|tombstone"}
versioned_cache_operation_duration_seconds
```

Redis errors не скрываются и не превращаются в cache miss; retry — ответственность
приложения или queue consumer. Повреждённая Redis Hash запись вызывает
`VersionedCacheCorruptedEntryException` без включения payload в сообщение.

## CI и публикация

`ci.yml` при push и pull request собирает solution, запускает unit и Redis
Testcontainers тесты, затем собирает NuGet-пакет. Публикация из `publish.yml`
выполняется только после merge pull request в `main` и повторяет эти проверки.

Для публикации настройте GitHub environment `production`, секрет `NUGET_USER` с
именем аккаунта NuGet.org (не email) и NuGet.org Trusted Publisher для этого
репозитория, workflow `publish.yml` и environment `production`. Перед новым выпуском
обновите `Version` в `src/MonixOne.VersionedCache/MonixOne.VersionedCache.csproj`.
