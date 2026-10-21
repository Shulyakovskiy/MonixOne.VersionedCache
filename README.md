# MonixOne.VersionedCache

`MonixOne.VersionedCache` — небольшой NuGet-пакет для атомарного хранения
версионированных Redis read-model. Он гарантирует, что запись со старой версией не
перезапишет более новую запись независимо от порядка доставки, duplicate delivery и
количества реплик.

Redis остаётся disposable cache. PostgreSQL (или другая основная БД) — source of truth.

## Установка и регистрация

Для всего публичного API достаточно одного `using MonixOne.VersionedCache;`.

Приложение само создаёт и регистрирует один `IConnectionMultiplexer`; пакет не открывает
соединения на каждый запрос.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

builder.Services.AddVersionedCache();
```

`IVersionedCache` и `RedisVersionedCache` имеют singleton lifetime и не хранят
request-specific state.

## Конфигурация

В `appsettings.json` сервиса укажите его уникальное имя и подключение к Redis:

```json
{
  "Service": {
    "Name": "identity-service"
  },
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  }
}
```

`Service:Name` обязателен и автоматически читается из `IConfiguration`. Окружение
автоматически берётся из `IHostEnvironment.EnvironmentName`. Для регистрации достаточно
`builder.Services.AddVersionedCache()`; вручную передавать env и имя сервиса не нужно.
Приложение регистрирует `IConnectionMultiplexer`, как показано выше.

Для локального запуска задайте окружение через стандартный .NET host:

```sh
DOTNET_ENVIRONMENT=Development dotnet run
```

В deployment эти настройки можно передать переменными окружения:

```text
DOTNET_ENVIRONMENT=Production
Service__Name=identity-service
ConnectionStrings__Redis=redis:6379
```

`__` разделяет уровни конфигурации: `Service__Name` соответствует `Service:Name`.
ASP.NET Core также поддерживает `ASPNETCORE_ENVIRONMENT`. Библиотека использует
окружение и конфигурацию host, а не читает переменные окружения самостоятельно.

## Изоляция окружений и сервисов

Библиотека добавляет префикс `{environment}:{service}:` ко всем Redis-ключам при записи,
чтении, `GetManyAsync` и записи tombstone. Один логический ключ `profile:v1:{id}`
хранится отдельно для каждой пары окружения и сервиса:

```text
Development:identity-service:profile:v1:{id}
Production:identity-service:profile:v1:{id}
Production:profile-service:profile:v1:{id}
```

Все экземпляры и consumers одного сервиса в одном окружении должны использовать
одинаковые имена, с учётом регистра. Разным сервисам задавайте разные `Service:Name`.
В каждом deployment проверьте имя окружения host: стандартное значение по умолчанию —
`Production`.

Для обоих имён запрещены пустое значение, пробельные символы, `:`, `{` и `}`. Настройки
проверяются при запуске host и при создании cache. Отсутствующие `Service:Name`,
`IHostEnvironment` или `IConfiguration` приводят к ошибке. При прямом создании
`RedisVersionedCache` через `new` нужно явно задать `VersionedCacheOptions.EnvironmentName`
и `ServiceName`.

Префикс сохраняет hash tag сущности для Redis Cluster. В API передавайте логические
ключи без env и имени сервиса; `GetManyAsync` возвращает словарь с исходными ключами.

При обновлении до `2.0.0` проверьте окружение и `Service:Name`. Старые
ключи без нового префикса `{environment}:{service}:` не читаются и не изменяются: cache
наполняется заново из основной БД или событий, старые записи остаются до истечения
своего TTL. Перед переключением учитывайте потерю доступа к старым tombstone:
восстановите актуальные версии из source of truth, прежде чем обрабатывать запоздалые
события.

## Использование

```csharp
var key = $"profile:v1:{{{profileId}}}";

var result = await versionedCache.SetAsync(
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

Формат логического ключа рекомендуемый, но не навязывается библиотекой:

```text
{projection}:v{schemaVersion}:{entityId}
```

`v1` в ключе — версия схемы cache model. Поле Redis `v` — монотонная версия конкретной
сущности. Для Redis Cluster полезно поместить `entityId` в hash tag: `{entityId}`.
Не включайте PII в cache key.

## Удаление

Удаление — это tombstone, а не `DEL`: он сохраняет версию и не позволяет запоздалому
событию воскресить удалённую сущность.

```csharp
await versionedCache.DeleteAsync(
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
