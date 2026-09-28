# Аутентификация и авторизация «Местной площадки Genesis» — состояние на 2026-09-24

Документ собран для проектирования отдельного сервиса **Genesis ID** (единый
аккаунт/SSO экосистемы). Это снимок того, **как есть**, а не предложение, как
должно быть. Ничего в ходе работы не менялось: код, конфиги, миграции и база не
затрагивались, найденные проблемы описаны, но не исправлены.

Значения секретов здесь отсутствуют намеренно — приводятся только имена
конфиг-ключей и места их чтения.

**Периметр.** Сервер — `SERVER/Genesis-Pridnestrovie-Market-SERVER` (ветка
`master`, последний коммит `e93f064`). Фронтенд — отдельное дерево
`Frontend/genesis_market`, отдельный git-репозиторий; разобран на уровне
хранения токенов и auth-флоу.

**Незакоммиченная работа.** В рабочем дереве лежит невлитая функция «вход по
одноразовому коду на email»: новый файл `Api/Auth/LoginCodeService.cs` и правки
`AuthController.cs`, `AuthRateLimiter.cs`, `AuthDtos.cs`, `Enums.cs`,
`VerificationService.cs`, `VerificationSender.cs`, `VerificationEmailRenderer.cs`,
`EmailTemplates/verification-code.html`, `AuthTests.cs`. Она включена в отчёт и
всюду помечена значком **(WIP)**. Её поведение до влития может измениться.

---

## 1. Стек аутентификации

**ASP.NET Core Identity не используется.** Реализация собственная. Это не
догадка: пакетов `Microsoft.AspNetCore.Identity*` нет ни в одном `.csproj`, и то
же зафиксировано комментариями в трёх местах — `Domain/Entities/User.cs:7`,
`Api/Auth/AuthServiceCollectionExtensions.cs:12-13`,
`Api/Controllers/AuthController.cs:18`.

Argon2, PBKDF2, Isopoh, libsodium в зависимостях также отсутствуют.

### Платформа

`Directory.Build.props:4-13`: `net10.0`, `Nullable enable`,
`TreatWarningsAsErrors`, `InvariantGlobalization`.

### Пакеты, относящиеся к auth

| Пакет | Версия | Где |
|---|---|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.0 | `Api/GenesisMarket.Api.csproj:11` |
| `BCrypt.Net-Next` | 4.0.3 | `Infrastructure/GenesisMarket.Infrastructure.csproj:18` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.0 | `Infrastructure/…csproj:14` |
| `FluentValidation` | 11.11.0 | `Api/…csproj:14-15` |
| `Polly` | 7.2.4 | `Api/…csproj:29` |

`GenesisMarket.Domain` не имеет пакетных зависимостей вовсе.

### Точка регистрации

`Program.cs:96` → `builder.Services.AddGenesisAuth(builder.Configuration, builder.Environment)`,
реализация — `Api/Auth/AuthServiceCollectionExtensions.cs` (152 строки). Там же
регистрируются `ICurrentUser`, `IRefreshTokenService`, `ITokenService`,
`IPublicCodeGenerator`, `VerificationService`, `LoginCodeService` (WIP),
`PasswordResetService`, `IAuthRateLimiter`, `IIpHasher`, `SecurityStampValidator`.

### Порядок middleware (`Program.cs:149-196`)

```
149  UseForwardedHeaders()          — первым, дальше RemoteIpAddress и IsHttps реальные
150  UseExceptionHandler()
152  SecurityHeadersMiddleware      — рано, чтобы попадать и на ответы-ошибки
153  RequestIdEnricherMiddleware
154  UseSerilogRequestLogging()
157  (Development) UseSwagger / UseSwaggerUI
163  UseCors(corsPolicy)
165  UseAuthentication()
166  UseAuthorization()
169  UseRateLimiter()               — после аутентификации: политики «на пользователя» видят identity
173  SessionActivityMiddleware      — после лимитера: 429 активностью не считается
175  MapControllers()
184  /health/live, /health/ready    — AllowAnonymous + DisableRateLimiting
```

### Fail-fast на старте

- Пустой `Jwt:Key` → исключение с текстом про переменную `Jwt__Key`
  (`AuthServiceCollectionExtensions.cs:30-32`); ключ короче 32 байт → исключение
  (`:33-35`).
- Вне Development пустой `Smtp:Host` роняет старт (`:80-86`) — чтобы код
  подтверждения не писался в лог вместе с адресом получателя.
- `Api/Configuration/OptionsValidationSetup.cs`: наличие любого из секретов
  (`Jwt:Key`, `Postgres:Password`, `Minio:SecretKey`, `Smtp:Password`,
  `Security:IpHashKey`, `Telegram:BotToken`, `Resend:ApiKey`) в
  `appsettings*.json` роняет старт; в Production дополнительно проверяются
  недефолтный пароль БД, наличие `Security:IpHashKey` и непустой валидный
  `Cors:AllowedOrigins`.

---

## 2. Модель пользователя

### Таблица `users`

Сущность — `Domain/Entities/User.cs:10` (`class User : BaseEntity`), конфигурация
EF — `Infrastructure/Persistence/Configurations/UserConfiguration.cs`, итоговое
состояние схемы — `Persistence/Migrations/AppDbContextModelSnapshot.cs:1425-1516`.

**Первичный ключ — `Guid` (UUID v7)**, генерируется приложением, не БД:
`Domain/Common/BaseEntity.cs:10` (`Guid.CreateVersion7()`).

| Колонка | C# | PostgreSQL | Default | Примечание |
|---|---|---|---|---|
| `Id` | `Guid` | `uuid` PK | — | UUID v7 |
| `Email` | `required string` | `varchar(256)` NOT NULL | — | уникальный индекс |
| `PasswordHash` | `required string` | `varchar(200)` NOT NULL | — | BCrypt, наружу не отдаётся |
| `PublicCode` | `required string` | `varchar(5)` NOT NULL | — | уникальный индекс |
| `Role` | `UserRole` | `varchar(20)` NOT NULL | `'User'` | строкой, не native enum |
| `PhoneE164` | `string?` | `varchar(20)` | — | |
| `PhoneVerified` | `bool` | `boolean` | `false` | |
| `EmailVerified` | `bool` | `boolean` | — | **без `DEFAULT false`** |
| `SecurityStamp` | `Guid` | `uuid` | — | меняется при смене пароля/выходе везде |
| `AverageRating` | `double?` | `double precision` | — | `private set`, триггер БД |
| `ReviewsCount` | `int` | `integer` | `0` | `private set`, триггер БД |
| `ApprovedListingsCount` | `int` | `integer` | `0` | `private set`, триггер БД |
| `LastRejectedAt` | `DateTimeOffset?` | `timestamptz` | — | `private set`, триггер БД |
| `WarningsCount` | `int` | `integer` | — | `private set`, только через `Warn()` |
| `LastWarnedAt` | `DateTimeOffset?` | `timestamptz` | — | `private set` |
| `IsBanned` | `bool` | `boolean` | `false` | |
| `BannedUntil` | `DateTimeOffset?` | `timestamptz` | — | |
| `IsDeleted` | `bool` | `boolean` | — | мягкое удаление; **без `DEFAULT false`** |
| `CreatedAt` | `DateTimeOffset` | `timestamptz` | — | из `BaseEntity` |
| `UpdatedAt` | `DateTimeOffset?` | `timestamptz` | — | из `BaseEntity` |

**Индексы:** `IX_users_Email` UNIQUE, `IX_users_PublicCode` UNIQUE
(`UserConfiguration.cs:16, 26`). **CHECK-ограничений на `users` нет.**

Имена таблиц — snake_case (задаются вручную через `ToTable`), имена колонок —
PascalCase (дефолт EF, в SQL в кавычках).

Денормализованные счётчики (`AverageRating`, `ReviewsCount`,
`ApprovedListingsCount`, `LastRejectedAt`) поддерживаются **триггерами
PostgreSQL** — `reviews_rating_sync` (миграция `20260811174851_AddTrustLayer.cs:135-156`)
и `listings_trust_sync` (`20260907184710_AddModerationTrustSignals.cs:57-137`).
Из приложения они не пишутся (`private set`). Для Genesis ID это важно: эти
колонки живут в торговом домене, а не в домене идентичности.

### Смежные таблицы

**`profiles`** (`Domain/Entities/Profile.cs`, `ProfileConfiguration.cs`) — 1:1,
PK = FK = `UserId`, каскад. Презентационная часть: `DisplayName varchar(60)`,
`City` (native enum `city`), `AvatarUrl`, `TelegramUsername`, `Description varchar(300)`,
`NotifyVia`, `TelegramChatId`, `ViberEnabled`, `WhatsappEnabled`,
`ShowPhoneInListing`. CHECK `ck_profiles_display_name_length`.

**`business_profiles`** (`BusinessProfile.cs`, `BusinessProfileConfiguration.cs`) —
1:1, PK = FK = `UserId`, каскад. `AccountType`, `ShopName`, `LegalForm`,
`RegistrationNumber`, `PickupAddress`, `Status` (**concurrency token**),
`RejectionReason`, `RejectionCount`, `SubmittedAt`, `ReviewedAt`,
`ReviewedByUserId` (без FK). Четыре CHECK, уникальный частичный индекс
`ux_business_profiles_verified_registration_number`.

**`refresh_tokens`** — см. раздел 7.
**`verification_codes`** — см. раздел 5.
**`password_reset_tokens`** — см. раздел 6.

### Чего нет

- **Отдельной таблицы ролей нет.** Роль — колонка `users.Role`, строка.
  Нет ни `roles`, ни `user_roles`, ни claims-таблиц.
- **Отдельной таблицы сессий нет.** Сессия живёт колонками `refresh_tokens`
  (`SessionId`, `SessionStartedAt`, `LastSeenAt`, `DeviceFamily`,
  `BrowserFamily`, `OsFamily`, `IpPrefix`).
- **Таблицы предупреждений и банов нет.** Предупреждения — счётчик и дата
  в `users`; баны — `IsBanned`/`BannedUntil`. История с причинами — append-only
  `moderation_logs` (действия `user.ban`, `user.unban`, `user.warn`).
- **Внешних провайдеров (OAuth, соцсети) нет.**

### Роли

`Domain/Enums/Enums.cs:74-79`:

```csharp
public enum UserRole { User, Moderator, Admin }
```

В БД — строкой (`HasConversion<string>()`, `UserConfiguration.cs:29-33`),
`varchar(20)`, default `'User'`. **Ни один enum в проекте не хранится как int.**

---

## 3. Публичный ID

### Что хранится

**Пять голых цифр, без префикса.** Колонка `users.PublicCode`, тип `varchar(5)`,
диапазон значений `10000`–`99999` (ведущих нулей нет).

Почему `varchar(5)`, а не `char(5)` — объяснено в `UserConfiguration.cs:20-24`:
у `char` в PostgreSQL дополнение пробелами и обрезка хвостовых пробелов при
сравнении, для кода фиксированной длины это лишняя семантика.

**Префикса `Gen-`/`GEN-`/`#` в базе и в API нет.** Это зафиксировано как решение
в `Api/Contracts/ProfileDtos.cs:9-11`:

> `PublicCode` — пять голых цифр. Оформление («GEN-82914», «#GEN-82914») — забота
> клиента: префикс это брендинг, а не данные, и API не должен переучиваться при
> его смене.

### Алгоритм генерации

`Api/Auth/PublicCodeGenerator.cs`:

- Границы диапазона — в одном месте (`:33-41`): `Min = 10_000`, `Max = 99_999`,
  `Length = 5`, `PoolSize = 90_000`.
- Источник случайности — **криптографический**, `RandomPublicCodeSource` (`:19-24`):
  `RandomNumberGenerator.GetInt32(PublicCode.Min, PublicCode.Max + 1)`.
  Не `Random`, не последовательность. Интерфейс `IPublicCodeSource` вынесен ради
  подмены детерминированной последовательностью в тестах
  (`tests/GenesisMarket.Tests/ScriptedPublicCodeSource.cs`).
- Подбор свободного — `NextAsync` (`:56-66`): до `MaxAttempts = 10` попыток,
  каждая проверяется `db.Users.AnyAsync(u => u.PublicCode == code)`; при неудаче —
  `PublicCodeExhaustedException` (`:74-75`), отдельный тип, чтобы в логе читалось
  «диапазон переполнен, нужна разрядность».

Осознанный отказ выводить код из `Id` объяснён в `User.cs:22-25`: `Id` — UUID v7,
то есть таймстамп регистрации, а последовательный номер выдал бы и порядок
регистрации, и общее число аккаунтов.

### Как обеспечивается уникальность

Три независимых уровня:

1. Проверка занятости перед вставкой — `PublicCodeGenerator.cs:61`.
2. **Уникальный индекс `IX_users_PublicCode`** — `UserConfiguration.cs:26`.
3. **Ретрай на конфликт** — `AuthController.cs:349-370`. Проверка и вставка не
   атомарны, поэтому две одновременные регистрации могут выбрать один код и обе
   пройти проверку; проигравшая ловит `23505` по имени констрейнта и берёт
   следующий код (до 3 попыток), а не отдаёт 500:

```csharp
catch (DbUpdateException ex) when (attempt < maxRetries && IsPublicCodeConflict(ex))
    user.PublicCode = await publicCodes.NextAsync(ct);
```

Существующим пользователям коды розданы миграцией
`20260913112542_AddUserPublicCode.cs` в три шага: nullable-колонка → backfill
перестановкой `generate_series(10000, 99999)` с `row_number() OVER (ORDER BY random())`
(уникальность по построению, без циклов) → `NOT NULL` + уникальный индекс. В
миграции явно оговорено: при более чем 90 000 пользователей она падает, а не
выдаёт дубли.

Тесты: `tests/GenesisMarket.Tests/PublicCodeTests.cs` — коллизия дважды подряд
(`:27`), 10 занятых подряд → 500 и пользователь не создан (`:75`), форма кода
(`:102`), неизменность кода (`:186`).

### Где отдаётся

`UserResponse.PublicCode` (`Contracts/AuthDtos.cs:54`) — приходит вместе с
сессией, чтобы клиент сразу мог сверять владельца; `MeResponse.PublicCode`
(`ProfileDtos.cs:15`); `PublicProfileResponse.PublicCode` (`:103`);
`ListingResponse.OwnerPublicCode` (`ListingDtos.cs:25`);
`ReviewResponse.AuthorPublicCode` (`ReviewDtos.cs:30`);
`ReportDtos` (`:18, 32`); бизнес-DTO (`BusinessDtos.cs:88`); все модераторские
DTO (`ModerationDtos.cs:90, 133, 165`, `ModerationWorkspaceDtos.cs:15, 30, 164,
211, 287, 322, 335, 392`). URL аватара строится по коду, не по `Guid`
(`Profiles/AvatarUrl.cs:14-15`).

Резолвинг «код → `Guid`» — `Profiles/PublicCodeResolver.cs:19-34`: форма
проверяется до запроса (ровно 5 ASCII-цифр), затем поиск с фильтром `!IsDeleted`.
Поиск по коду на вход открыт только модератору
(`GET /api/moderation/users/by-code/{code}`), потому что 90 000 кодов —
перечислимое пространство имён (`ProfileDtos.cs:96-100`, тест `PublicCodeTests.cs:170`).

### Где отображается на фронтенде

Форматирование — **`Frontend/src/lib/format.ts:117-119`**:

```ts
export function formatPublicCode(code: string): string {
  return `GEN-${code}`;
}
```

Используется в `components/user/CopyPublicCode.tsx:14` (кнопка «скопировать ID»),
`components/profile/ProfileView.tsx:464` (свой профиль),
`components/user/PublicProfileView.tsx:117`, `components/store/StoreHeader.tsx:84`.

**Модераторские экраны префикс `GEN-` не используют** — выводят `#` + код:
`JournalView.tsx:204, 223`, `QueueView.tsx:355`, `UsersView.tsx:175, 303`,
`UserSummaryCard.tsx:60`, `ReportsView.tsx:204, 263`,
`BusinessApplicationsView.tsx:92`.

Код также служит идентификатором владельца для сверки «моё ли это»
(`PriceContacts.tsx:34`, `listing/[slug]/edit/page.tsx:33`, `ReviewCta.tsx:28`)
и маршрутом `/user/{publicCode}`.

### Единственное место, где сервер знает о префиксе

`Api/Moderation/ModerationSearch.cs:15-23` — разбор строки поиска модератора
снимает `GEN-` (регистронезависимо) и ведущий `#`. Больше нигде префикс не
снимается: `PublicCodeResolver.IsWellFormed` требует ровно 5 цифр, поэтому
строка с префиксом, посланная в `/api/users/by-code/{code}` или в
`targetPublicCode` жалобы, молча даст 404.

---

## 4. Пароли

**Это ключевой раздел для переноса: от него зависит, придётся ли сбрасывать
пароли всем пользователям.**

### Алгоритм

BCrypt, библиотека `BCrypt.Net-Next` 4.0.3. Реализация —
`Infrastructure/Auth/BcryptPasswordHasher.cs:24-28`:

```csharp
public string Hash(string password) => BC.EnhancedHashPassword(password, _workFactor);
public bool Verify(string password, string hash) => BC.EnhancedVerify(password, hash);
public bool NeedsRehash(string hash) => BC.PasswordNeedsRehash(hash, _workFactor);
```

### ⚠️ `Enhanced` — не стандартный bcrypt

`EnhancedHashPassword` в `BCrypt.Net-Next` **предварительно прогоняет пароль
через SHA-384 и кодирует результат в Base64**, и только потом отдаёт его bcrypt.
Сделано это, чтобы вход не обрезался молча на 72 байтах (комментарий
`BcryptPasswordHasher.cs:6-10`).

Практические следствия для Genesis ID:

- Строка хэша выглядит как совершенно обычный bcrypt: `$2a$12$<22 символа соли><31 символ хэша>`.
- **Но стандартная bcrypt-библиотека её не проверит.** `bcrypt.checkpw(password, hash)`
  в Python, `bcrypt.compare` в Node, `BCrypt.Verify` (не `EnhancedVerify`) в .NET —
  все вернут `false` для верного пароля.
- **Хэши переносимы без сброса паролей**, но новый сервис обязан либо
  использовать ту же библиотеку с `EnhancedVerify`, либо воспроизвести
  pre-hash: `bcrypt_verify(base64(sha384(password_utf8)), stored_hash)`.
- Это надо проверить экспериментально на копии данных до миграции — тихая
  несовместимость здесь означает, что войти не сможет никто.

### Параметры

- **Work factor: 12** — `Infrastructure/Auth/BcryptOptions.cs:9`, секция
  конфигурации `Bcrypt`, то же значение в `appsettings.json`.
- **Соль** — внутренняя bcrypt-соль 128 бит, генерируется библиотекой на каждый
  хэш, хранится внутри строки хэша. Отдельной колонки соли нет.
- **Колонка** — `users.PasswordHash varchar(200)` (`UserConfiguration.cs:18`).

### Версионирование и перехэширование

Отдельного поля версии нет — версия зашита в префикс строки хэша (`$2a$`).
Перехэш при изменении work factor — `AuthController.cs:117-118`:

```csharp
if (hasher.NeedsRehash(user.PasswordHash))
    user.PasswordHash = hasher.Hash(request.Password);
```

Сохраняется попутно, внутри `IssueAsync` (комментарий `:290`). Важно:
**перехэш происходит только при входе паролем**. При входе по коду (WIP) пароль
не предъявляется, поэтому и перехэшировать нечего.

### Защита от timing-атак

`BcryptPasswordHasher.cs:21` — `DummyHash` считается один раз при старте
приложения; когда пользователь не найден, `Login` всё равно выполняет проверку
против него (`AuthController.cs:105`), чтобы время ответа не выдавало
существование адреса.

### Политика паролей

`Infrastructure/Auth/PasswordPolicy.cs`: 8–72 **байта** UTF-8 + блок-лист частых
паролей (embedded resource `Auth/common-passwords.txt`,
`GenesisMarket.Infrastructure.csproj:9`). Статусы `TooShort`/`TooLong`/`TooCommon`
маппятся в 400 (`AuthController.cs:374-383`). Применяется при регистрации,
смене пароля и сбросе; **при входе не применяется** (это корректно — иначе
ужесточение политики заблокировало бы существующие аккаунты).

Замечание: ограничение «не длиннее 72 байт» — наследие стандартного bcrypt.
При `Enhanced`-варианте SHA-384 pre-hash снимает эту границу, так что верхний
предел здесь избыточен, хотя и безвреден.

---

## 5. Вход по одноразовому коду на email **(WIP — не в master)**

Реализация: `Api/Auth/LoginCodeService.cs` (новый файл) + общие примитивы из
`Api/Auth/VerificationService.cs`.

### Генерация кода

`VerificationService.cs:131-137`:

```csharp
internal static string GenerateCode(int length) {
    var sb = new StringBuilder(length);
    for (var i = 0; i < length; i++)
        sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
    return sb.ToString();
}
```

Алфавит — только цифры 0–9; длина — `VerificationOptions.CodeLength` = **6**;
источник — криптографический `RandomNumberGenerator`, не `Random`. Ведущие нули
сохраняются, поскольку код собирается как строка посимвольно.

### Хранение — хэшем, но без ключа

`LoginCodeService.cs:63` → `CodeHash = VerificationService.Hash(code)`, где
`VerificationService.cs:139`:

```csharp
internal static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
```

Сам код в базу не попадает. **Но это «голый» SHA-256 без ключа и без соли.**
Пространство — 10⁶ значений; полная радужная таблица строится за секунды.
Это прямое расхождение с собственным техзаданием — см. раздел «Риски».

Коды живут в общей таблице `verification_codes`, но в отдельном канале
`VerificationChannel.LoginEmail` (`LoginCodeService.cs:28`, enum —
`Enums.cs:85-95`), чтобы код входа и код подтверждения почты не принимались друг
за друга и не делили кулдаун.

### Параметры

`Api/Auth/VerificationOptions.cs:11-14`, секция конфигурации `Verification`:

| Параметр | Значение |
|---|---|
| `CodeLength` | 6 |
| `CodeTtlMinutes` | 5 |
| `MaxAttempts` | 5 |
| `ResendCooldownSeconds` | 60 |

### Лимиты и троттлинг

- **Кулдаун на адрес** — `LoginCodeService.cs:42-50`: берётся `CreatedAt`
  последнего кода канала; если прошло меньше 60 с, метод **молча выходит**.
  Снаружи это неотличимо от успеха. Кулдаун считается по БД, а не по счётчику
  в памяти — переживает рестарт контейнера.
- **Лимит на IP** — `AuthRateLimiter.cs:30-31`: 10 запросов кода в час.
- **Лимит попыток ввода** — `LoginCodeService.cs:100` (`entry.Attempts >= MaxAttempts`),
  инкремент на каждом промахе (`:105`).
- **Лимит на проверку кода** — общий с входом по паролю, 5 попыток / 15 мин
  на пару (IP, email), `AuthController.cs:153`.

### Проверка

`LoginCodeService.VerifyAsync` (`:85-114`):

1. Найти пользователя по нормализованному адресу; нет — `null`.
2. Взять последний непогашенный код канала (`OrderByDescending(CreatedAt)`).
3. Отказать, если кода нет, истёк или исчерпаны попытки.
4. Сравнить в постоянном времени: `CryptographicOperations.FixedTimeEquals` (`:103`).
5. Промах → `Attempts++`, `null`.
6. Успех → `ConsumedAt = now`, **`user.EmailVerified = true`** (`:111`) — код,
   пришедший на почту, заодно подтверждает владение ею.

### Инвалидация

При выпуске нового кода все прежние непогашенные коды этого канала гасятся
одним `ExecuteUpdateAsync` (`LoginCodeService.cs:53-55`). Использованный код
гасится `ConsumedAt` — повторно не сработает.

### Анти-перечисление

Выдержано последовательно: неизвестный адрес → тихий `return` (`:38-39`);
сбой отправки ловится и только логируется (`:72-77`), наружу 500 не уходит;
контроллер всегда отвечает одинаковым текстом (`AuthController.cs:138-139`), а
любой промах кодом даёт единый `LoginCodeError` (`:41`).

### Отправка писем

**Resend в аутентификации не участвует вообще.** Все auth-письма уходят через
SMTP: `Api/Auth/EmailSender.cs`, класс `SmtpEmailSender` (`:44-85`), на
`System.Net.Mail`. Multipart: text первым, html последним, логотип inline как
`LinkedResource` с `cid`.

Resend (`Api/Feedback/ResendEmailService.cs`) используется **только** формой
обратной связи.

Выбор реализации — `AuthServiceCollectionExtensions.cs:72-86`: есть `Smtp:Host` →
`SmtpEmailSender`; нет, но Development → `LogEmailSender` (пишет письмо в лог);
нет и не Development → исключение на старте.

Имена конфиг-ключей (значения не приводятся): `Smtp:Host`, `Smtp:Port`,
`Smtp:User`, `Smtp:Password`, `Smtp:From`, `Smtp:FromName`, `Smtp:UseSsl`
(env: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM`,
`SMTP_FROM_NAME`, `SMTP_USE_SSL`). Для формы обратной связи — `Resend:ApiKey`,
`Resend:FromEmail`, `Resend:NotificationEmail` (env `RESEND_API_KEY`,
`RESEND_FROM_EMAIL`, `FEEDBACK_NOTIFICATION_EMAIL`).

### Шаблоны

Embedded resources (`Api/GenesisMarket.Api.csproj:43-45`), чтение —
`Api/Auth/EmailResources.cs:10-33`:

- `Auth/EmailTemplates/verification-code.html` — общий для кода подтверждения
  почты **и** кода входа; плейсхолдеры `{{TITLE}}`, `{{HEADING}}`, `{{HINT}}`,
  `{{IGNORE}}`, `{{CODE}}`, `{{LOGO_URL}}`.
- `Auth/EmailTemplates/password-reset.html` — `{{RESET_URL}}`, `{{TTL}}`, `{{LOGO_URL}}`.
- `Auth/EmailTemplates/gm-logo.png` — inline через `cid:gm-logo`.

Рендеринг — `VerificationEmailRenderer.cs` (два набора текстов: `Confirm` и
`Login`), выбор по каналу — `VerificationSender.cs:22-31`.

### Какие письма шлются вообще

1. Код подтверждения почты — при регистрации и по запросу из профиля.
2. Код входа (WIP).
3. Ссылка восстановления пароля.
4. Транзакционные уведомления через outbox: объявление одобрено / отклонено /
   на доработку / скоро в архив, новый отзыв (`Api/Outbox/OutboxHandlers.cs`).
5. Уведомления по сохранённым поискам.
6. Обратная связь (через Resend).

SMS-канал: реального провайдера нет. В Development код пишется в лог
(`DevSmsSender`), вне Development канал честно отвечает 503
(`UnavailableSmsSender`, `AuthServiceCollectionExtensions.cs:67-70`).

---

## 6. Флоу

### 6.1. Регистрация — есть

`AuthController.Register` (`:43-85`), `POST /api/auth/register`, анонимно,
3 запроса/час на IP.

1. Валидация пароля → 400.
2. Нормализация телефона (`PhoneNumber.Normalize`) → 400 при некорректном.
3. Нормализация email: `Trim().ToLowerInvariant()` (`:372`).
4. **Хэш пароля считается всегда, до проверки занятости** (`:58`) — чтобы ответ
   на занятый адрес не отличался по времени.
5. Проверка занятости; если свободен — создаются `User` + `Profile`.
   `Role = UserRole.User` жёстко, поле из запроса игнорируется (`:67`).
   `PublicCode` — из генератора (`:70`).
6. Сохранение с ретраем публичного кода (`:78`).
7. Отправка кода подтверждения почты (`:79`). **Сбой отправки регистрацию не
   отменяет** — аккаунт уже сохранён, код перезапрашивается из профиля.
8. Ответ **одинаков для свободного и занятого адреса** (`:83-84`).

**Токены при регистрации не выдаются.** Фронтенд после успешной регистрации сам
делает обычный вход (`Frontend/src/lib/auth/AuthProvider.tsx:166-179`).

### 6.2. Подтверждение email — есть

- `POST /api/me/email/send-code` → `VerificationService.SendAsync` (`:28-82`).
  Уже подтверждён → 409; кулдаун → 429 с `Retry-After`; канал недоступен → 503.
- `POST /api/me/email/verify` → `VerificationService.VerifyAsync` (`:84-117`).
  Истёк → 400; исчерпаны попытки → 400; неверный → инкремент + 400;
  успех → `EmailVerified = true` + `ConsumedAt`.

Обе ручки требуют `[Authorize]` (`EmailVerificationController.cs:13`) — то есть
подтверждение делается из профиля, уже войдя.

Зачем нужно: `Api/Auth/PublishingPolicy.cs:34-43` — публикация объявлений и
раскрытие контактов требуют подтверждённой почты (конфиг
`Publishing:RequiredVerification`).

Альтернативный путь подтверждения — успешный вход по коду (WIP).

### 6.3. Вход паролем — есть

`AuthController.Login` (`:87-122`), `POST /api/auth/login`, анонимно.

Лимит (IP, email) → 429 + `Retry-After`; пользователь не найден → проверка против
`DummyHash` + 401 с общим текстом «Неверный email или пароль»; неверный пароль →
тот же 401 с тем же текстом; рехэш при необходимости; выдача сессии.

`IssueAsync` (`:288-297`): сперва `refreshTokens.IssueAsync` (нужен `sessionId`),
затем `tokenService.CreateAccessToken(user, sessionId)`.

⚠️ **Забаненный и удалённый на входе не отсекаются.** Блокировка наступает
позже, на валидации access-токена (`SecurityStampValidator`). То есть
забаненному выдаётся валидный refresh-токен. См. «Риски».

### 6.4. Вход по коду — есть **(WIP)**

Два шага: `POST /api/auth/login-code` (`:128-140`) →
`POST /api/auth/login-code/verify` (`:146-166`). Подробности — раздел 5.

### 6.5. Обновление токена — есть

`POST /api/auth/refresh` (`:168-189`), анонимно, refresh в теле JSON.
Подробности — раздел 7.

### 6.6. Выход — есть, три варианта

- `POST /api/auth/logout` (`:191-198`) — отзывает только переданный refresh.
  Результат намеренно не раскрывается. Access доживает свои ≤15 минут.
- `POST /api/auth/logout-all` (`:200-215`) — `RevokeAllAsync` + смена
  `SecurityStamp` + сброс кэша валидатора. Все access-токены умирают мгновенно.
- `DELETE /api/me/sessions/{id}` (`MeController.cs:242-251`) — отзыв одного
  устройства. Чужая сессия даёт 404, а не 403; фильтр по `UserId` — внутри
  самого запроса (`RefreshTokenService.cs:186`).

### 6.7. Смена пароля — есть

`POST /api/auth/change-password` (`:217-240`), требует авторизации.
Проверка текущего пароля → 400 при несовпадении; валидация нового; новый хэш;
новый `SecurityStamp`; `RevokeAllAsync`; сброс кэша; аудит.
**Разлогинивает все сессии, включая текущую** — фронтенд это учитывает
(`Frontend/src/lib/auth/actions.ts:87-88`).

### 6.8. Сброс пароля — есть

`Api/Auth/PasswordResetService.cs`.

- `POST /api/auth/forgot-password` (`:246-255`), анонимно, 3/час на IP →
  `RequestAsync` (`:39-90`): неизвестный адрес → тихий `return`; кулдаун 60 с;
  прошлые ссылки гасятся; токен — 32 случайных байта, наружу base64url, в БД
  SHA-256; TTL 30 минут; пишется `RequestedByIpHash`; сбой SMTP только
  логируется. Ответ всегда одинаков.
- Ссылка: `Seo:WebBaseUrl` + `PasswordReset:ResetPath` (`/reset-password`) + `?token=`.
- `POST /api/auth/reset-password` (`:264-284`), анонимно, **намеренно без
  отдельного лимита** (комментарий `:261-263`: подобрать 256-битный токен нельзя,
  а лимит 3/час блокировал бы человека, дважды промахнувшегося мимо требований к
  паролю) → `ResetAsync` (`:92-118`): новый хэш, новый `SecurityStamp`,
  `ConsumedAt`, отзыв всех refresh-токенов. 204 / 410 Gone (истекла или
  использована) / 400.

### 6.9. Смена email — **НЕТ**

Флоу смены адреса в коде отсутствует целиком: нет эндпоинта, нет DTO, нет UI.
`PATCH /api/me` email не биндит — явный список полей в `MeController.cs:52-57`,
комментарий `:51`: «Role/Email/IsBanned/… сюда не биндятся».

Единственное место, где `User.Email` меняется, — анонимизация при удалении
аккаунта: `MeController.cs:267`.

Практически это значит, что сменить адрес сегодня можно только заведя новый
аккаунт. Для Genesis ID это функция, которую придётся проектировать с нуля.

### 6.10. Смена телефона — есть, частично

`MeController.cs:74-86`: новый номер нормализуется, при изменении сбрасывается
`PhoneVerified = false`. Подтверждение — через `/api/me/phone/*`, но
SMS-провайдера нет, поэтому вне Development канал отвечает 503.

---

## 7. Сессии и токены

### Access-токен: JWT, HS256

Выпуск — `Api/Auth/JwtTokenService.cs:26-52`.

- **Алгоритм подписи:** HS256 (`SecurityAlgorithms.HmacSha256`, `:47`).
  На стороне валидации список сужен до него же
  (`ValidAlgorithms = [HmacSha256]`, `AuthServiceCollectionExtensions.cs:114`) —
  подмена на `none` или RS256 закрыта.
- **Ключ:** `JwtOptions.Key`, конфиг-ключ `Jwt:Key`, env `Jwt__Key`
  (в compose подставляется из `JWT_SECRET`). Читается только из окружения;
  наличие в `appsettings*.json` роняет старт. Минимум 32 байта, проверяется на
  старте.
- **Issuer / Audience:** `Jwt:Issuer`, `Jwt:Audience` (env `JWT_ISSUER`,
  `JWT_AUDIENCE`), оба валидируются.
- **Срок жизни:** `Jwt:AccessTokenMinutes` = **15 минут** (`JwtOptions.cs:18`).
- **`ClockSkew = TimeSpan.Zero`** (`:113`) — без стандартного пятиминутного люфта.
- **Claims** (`JwtTokenService.cs:39-46`):

| claim | значение |
|---|---|
| `sub` | `user.Id` (Guid) |
| `role` | `user.Role.ToString()` |
| `sstamp` | `user.SecurityStamp` (Guid) |
| `sid` | идентификатор сессии |
| `jti` | 32 случайных hex-символа |

Плюс стандартные `iss`, `aud`, `iat`, `nbf`, `exp`. **Email в токен не кладётся**
намеренно (комментарий `:20-21`).

`MapInboundClaims = false` (`:103`) — claims читаются как есть, без маппинга в
длинные URI. `NameClaimType = "sub"`, `RoleClaimType = "role"`.

### Дополнительная проверка на каждом запросе

`Api/Auth/SecurityStampValidator.cs`, подключён через
`JwtBearerEvents.OnTokenValidated` (`AuthServiceCollectionExtensions.cs:119-127`):

- некорректный `sub`/`sstamp` → отказ;
- `IsDeleted` → «Аккаунт удалён»;
- `sstamp` не совпал с БД → «Токен отозван»;
- забанен и бан не истёк → «Пользователь заблокирован».

Снимок кэшируется на `Jwt:SecurityStampCacheSeconds` = 30 с; `Invalidate(userId)`
сбрасывает кэш немедленно (вызывается из `logout-all` и `change-password`).
Именно поэтому политика `NotBanned` сведена к `RequireAuthenticatedUser`.

### Refresh-токен: есть

`Api/Auth/RefreshTokenService.cs`, таблица `refresh_tokens`.

- **Генерация** (`:212-217`): 32 случайных байта из `RandomNumberGenerator`,
  наружу base64url, **в БД — SHA-256** (`:219-220`). Сырой токен отдаётся
  ровно один раз, в теле ответа.
- **Срок жизни:** `Jwt:RefreshTokenDays` = **30 дней**, причём заново при каждой
  ротации (`:148`) — то есть сессия скользящая.
- **Ротация при каждом обновлении** (`RotateActiveAsync`, `:143-177`): создаётся
  новая строка, старая получает `RevokedAt` и `ReplacedByTokenId`.
  `SessionId` и `SessionStartedAt` переносятся, чтобы устройство не выглядело
  новым каждые 15 минут.
- **Обнаружение повторного использования** (`RotateAsync`, `:91-139`):
  - токен отозван **явно** (`ReplacedByTokenId is null`, то есть logout или
    отзыв сессии) → просто `Invalid`, цепочка не рушится. Объяснение `:101-107`:
    иначе «выйти на этом устройстве» работало бы как «выйти везде»;
  - токен отозван **ротацией** и предъявлен снова в пределах
    `Jwt:RefreshReuseGraceSeconds` = **60 с** → считается гонкой вкладок,
    отдаётся продолжение живой цепочки (`:119-126`);
  - за окном → `RevokeAllAsync` по всему пользователю + warning в лог +
    статус `Reuse` (`:128-132`).
- **Отзыв:** `RevokeAsync` (один), `RevokeAllAsync` (все), `RevokeSessionAsync`
  (одна сессия, с фильтром по владельцу внутри запроса).

### Таблица `refresh_tokens` — она же таблица сессий

| Колонка | Тип | Назначение |
|---|---|---|
| `Id` | `uuid` PK | UUID v7 |
| `UserId` | `uuid` | FK, каскад |
| `TokenHash` | `bytea` | SHA-256, уникальный индекс |
| `ExpiresAt`, `CreatedAt`, `RevokedAt` | `timestamptz` | |
| `ReplacedByTokenId` | `uuid` | цепочка ротации, **FK нет** |
| `CreatedByIpHash` | `varchar(128)` | HMAC-SHA256 от IP, индекс |
| `SessionId` | `uuid` | устойчив к ротации |
| `SessionStartedAt` | `timestamptz` | момент входа с устройства |
| `LastSeenAt` | `timestamptz` | обновляется не чаще 1 раза в 5 мин |
| `DeviceFamily`, `BrowserFamily`, `OsFamily` | `varchar(32)` | из User-Agent |
| `IpPrefix` | `varchar(39)` | два октета IPv4 / два хекстета IPv6 |

Индексы: `TokenHash` UNIQUE, `CreatedByIpHash`, `UserId`, `(UserId, SessionId)`.

**Сырой User-Agent не хранится** — разбирается на границе (контроллер) и дальше
не передаётся (`AuthController.cs:299-305`). **Сырой IP не хранится** — только
HMAC (`Api/Auth/IpHasher.cs`, ключ `Security:IpHashKey`, env `IPHASH_KEY`;
без ключа IP не сохраняется вовсе) и два октета для показа владельцу.

Просмотр и отзыв сессий: `GET /api/me/sessions`, `DELETE /api/me/sessions/{id}`
(`MeController.cs:204-251`), текущее устройство помечается по claim `sid`.

### Cookie: сервер не ставит ни одной

Поиск по всему `src/` на `Response.Cookies`, `CookieOptions`, `AddCookie`,
`UseCookiePolicy`, `SameSite`, `CookieAuthenticationDefaults` не даёт ни одного
совпадения в коде. Единственная схема аутентификации — `JwtBearer`.
Антифоржери (`AddAntiforgery`) не подключён — он и не нужен при bearer-модели.

Оба токена ходят в теле JSON: `AuthResponse(AccessToken, AccessTokenExpiresAt,
RefreshToken, RefreshTokenExpiresAt, User)` (`Contracts/AuthDtos.cs:69-74`);
refresh приходит обратно в теле (`RefreshRequest`, `LogoutRequest`).

**Для Genesis ID это чистый лист:** никакой серверной cookie-логики, никаких
доменов, `SameSite` и `Secure` на стороне API сегодня не существует.

### Фронтенд: трёхуровневая схема хранения

1. **Access-токен — только в памяти JS-модуля.**
   `Frontend/src/lib/api/http.ts:32` — `let accessToken: string | null = null;`,
   обёртка `tokenStore` (`:38-53`) с запасом `EXPIRY_SKEW_MS = 30_000`.
   В `localStorage` не кладётся ничего (`lib/auth/session.ts:5-6`).

2. **Refresh-токен — httpOnly-cookie `gm_refresh`**, которую ставит
   route handler Next.js `src/app/api/session/route.ts`:
   `httpOnly: true`, `secure` в production, `sameSite: "lax"`, `path: "/"`,
   `maxAge` из `refreshTokenExpiresAt` (`:53-64`). Домен не задаётся — cookie
   host-only на домене фронтенда.

3. **CSRF — читаемая cookie `gm_csrf` + заголовок `X-CSRF-Token`**
   (double-submit, `:11-13`, `:26-30`). Проверяется на `POST` (сохранение
   сессии — защита от session fixation) и на `DELETE` (выход чужими руками).
   Выдаётся и продлевается на `GET`, в том числе гостю.

Route handler `/api/session`:
- `POST` — сохранить refresh в cookie после входа;
- `GET` — сам ходит на бэкенд `POST /api/auth/refresh`, ротирует cookie и отдаёт
  клиенту только `accessToken`, `accessTokenExpiresAt` и `user`; гость → 204;
- `DELETE` — `POST /api/auth/logout` с bearer + refresh из cookie, затем чистит
  cookie.

Обновление — **single-flight** (`lib/auth/refresh.ts:31-47`): сколько бы запросов
ни упёрлось в 401, за новым токеном уходит ровно один. Это обязательное
требование из-за серверной ротации: два одновременных запроса с одним refresh
без окна гонки отозвали бы всю цепочку. Между вкладками cookie общая, и от гонки
это не спасает — там страхует серверное окно в 60 с.

Роль в теле логина не приходит (её нет в `UserResponse`) — подтягивается из
`GET /api/me`; до этого в клиенте стоит плейсхолдер `"User"` и флаг
`roleResolved`, чтобы роль-зависимый UI не мигал (`AuthProvider.tsx:44-55`).

---

## 8. Авторизация

### Роли

`User`, `Moderator`, `Admin`. Роль берётся **только** из claim `role` токена —
никогда из тела, заголовка или query. Через API роль не меняется: при
регистрации она жёстко `User` (`AuthController.cs:67`), в `UserResponse` её нет
(`AuthDtos.cs:44-46`), эндпоинта смены роли не существует. Назначение модератора
сегодня — прямая правка в БД.

### Политики (`AuthServiceCollectionExtensions.cs:130-148`)

| Политика | Требование | Применяется |
|---|---|---|
| `Moderator` | роль ∈ {Moderator, Admin} | да, на 7 контроллерах |
| `Admin` | роль = Admin | **нигде** |
| `NotBanned` | `RequireAuthenticatedUser()` | **нигде** |
| `ResourceOwner` | `ResourceOwnerRequirement` | один раз, явным вызовом |
| **FallbackPolicy** | `RequireAuthenticatedUser()` | на всё |

`FallbackPolicy` — ключевое решение: забытый `[Authorize]` не открывает
эндпоинт, публичные помечаются `[AllowAnonymous]` явно.

То, что `Admin` и `NotBanned` объявлены, но не применены, означает: **модератор и
администратор сегодня равноправны**, отдельного уровня «только админ» фактически
нет.

### Проверка владения ресурсом

**Обобщённый механизм** — `Api/Auth/ResourceOwnerAuthorization.cs:19-48`:

```csharp
var isModerator = currentUser.Role is UserRole.Moderator or UserRole.Admin;
var isOwner = currentUser.UserId is { } id && id == resource.OwnerId;
if (isModerator || isOwner) { context.Succeed(requirement); return; }
securityAudit.ResourceForbidden(actor, resource.GetType().Name, resourceId);
```

Работает для любого `IOwnedResource` (`Domain/Common/IOwnedResource.cs`) и пишет
попытки доступа к чужому в журнал безопасности. Единственный явный вызов —
`ListingsController.cs:502` (удаление объявления), где ответ 403, потому что
существование объявления публично.

**Остальные проверки — фильтром прямо в SQL-запросе**, что даёт 404 и на «нет
объекта», и на «объект чужой» (существование приватного ресурса не разглашается):

- объявления: `ListingsController.cs:445, 521, 568, 601, 627, 661, 704, 751`
- фото объявлений: `ListingImagesController.cs:77, 150, 181`
- сохранённые поиски: `SavedSearchesController.cs:45, 93, 145`
- избранное: `FavoritesController.cs:55, 86, 107`
- профиль, аватар, сессии: `MeController.cs:135, 178, 212, 296, 300`
- отзыв сессии: `RefreshTokenService.cs:186`
- отметка активности: `SessionActivityMiddleware.cs:53` (комментарий `:50-51` —
  «чтобы подделанный `sid` из чужого токена ничего не задел»)
- бизнес-профиль: `BusinessAccountService.cs:46`, `BusinessVerificationService.cs:108, 175`

Отзывы (`ReviewsController.cs`): нельзя оставить отзыв самому себе (`:55`),
один отзыв на объявление (`:62`), редактировать только свой (`:201`, здесь 403),
окно редактирования 24 часа (`:204`).

### Где стоят `[Authorize]`

Уровень контроллера: `AccountBusinessController.cs:22`,
`EmailVerificationController.cs:13`, `FavoritesController.cs:19`,
`MeController.cs:21`, `PhoneVerificationController.cs:13`,
`SavedSearchesController.cs:20` — просто `[Authorize]`;
`BusinessModerationController.cs:16`, `ModerationBlockedCardsController.cs:18`,
`ModerationController.cs:23`, `ModerationJournalController.cs:19`,
`ModerationQueueController.cs:21`, `ModerationReportsController.cs:18`,
`ModerationUsersController.cs:20` — `[Authorize(Policy = "Moderator")]`.

Уровень действия: `AuthController.cs:191, 200, 217`;
`ListingsController.cs:377, 437, 493, 515, 550, 595, 621, 655, 697, 741`;
`ListingImagesController.cs:66, 143, 173`; `ReviewsController.cs:41, 189` и
`:232` (`Moderator`).

Полностью анонимные контроллеры: `SeoController.cs:16`, `SitemapController.cs:15`,
`LinkRedirectController.cs:14`.

### Чтение текущего пользователя

Единственная точка — `Api/Auth/CurrentUser.cs:31-38`: `sub` → `UserId`,
`role` → `Role`, `sid` → `SessionId`. Доступ из контроллеров — через
`ApiControllerBase.CurrentUser` / `CurrentUserId()`.

`ClaimTypes.NameIdentifier` не используется нигде — `MapInboundClaims = false`
отключает маппинг, читаются сырые имена claims.

Два места читают claims напрямую, минуя `ICurrentUser`, потому что DI там
недоступен: `Security/RateLimitingSetup.cs:184-186` и
`Auth/SecurityStampValidator.cs:26-28`.

### Сверка с `docs/authorization-matrix.md`

Документ актуален по существу: политики, `FallbackPolicy`, модель
«владелец ИЛИ модератор», правило «приватное → 404, публичное → 403» описаны
верно. **Номера строк в нём частично разъехались** — файлы auth менялись после
его написания (в частности, `AuthController.cs` вырос с добавлением входа по
коду). При обновлении матрицы ссылки стоит перепроверить.

---

## 9. Инфраструктура

### CORS

`Program.cs:56-74`. Origin-ы — строкой через запятую из `Cors:AllowedOrigins`
(env `CORS_ALLOWED_ORIGINS`), `AllowAnyHeader`, `AllowAnyMethod`,
**`AllowCredentials()`**, `SetPreflightMaxAge` из `Cors:PreflightMaxAgeSeconds`
(сутки по умолчанию). `AllowAnyOrigin` не используется.

В Production список валидируется на старте (`OptionsValidationSetup.cs:76-86`):
пустой → отказ; каждый origin обязан быть абсолютным URI без завершающего слэша.

`UseCors` стоит до `UseAuthentication` (`:163`).

Замечание: `AllowCredentials()` включён при полностью bearer-модели без серверных
cookie — формально избыточное послабление.

### Cookie-домены

**На сервере их нет вообще** (см. раздел 7). Вся cookie-логика сегодня живёт в
Next.js, cookie host-only на домене фронтенда. Для будущих поддоменов
`*.genesis-hq.com` никаких унаследованных ограничений нет — но и никакой готовой
инфраструктуры тоже.

### HTTPS и прокси-заголовки

`Api/Security/NetworkSetup.cs`, подключается первым в пайплайне (`Program.cs:149`).

- Обрабатываются **только** `X-Forwarded-For` и `X-Forwarded-Proto`
  (`X-Forwarded-Host` — нет).
- Доверенные источники строго из конфигурации: `Network:KnownProxies`,
  `Network:KnownNetworks`, `Network:ForwardLimit` (по умолчанию 1).
  Env: `TRUSTED_PROXY_IPS`, `TRUSTED_PROXY_NETWORKS` (дефолт `172.16.0.0/12`),
  `TRUSTED_PROXY_FORWARD_LIMIT`.
- Дефолтные списки очищаются только если задан хотя бы один прокси или сеть;
  иначе остаётся дефолт ASP.NET (loopback).

Публикация — Cloudflare Tunnel: `cloudflared` на хосте отдаёт
`api.genesis-hq.com` в `127.0.0.1:8090`. Альтернатива на случай белого IP —
Caddy-overlay (`deploy/Caddyfile`, `header_up X-Real-IP`).

От корректного XFF зависят три вещи: ключи rate-limit, `CreatedByIpHash` в
сессиях и выставление HSTS (`SecurityHeadersMiddleware.cs:39` — только при
`Request.IsHttps`).

### Заголовки безопасности

`Api/Security/SecurityHeadersMiddleware.cs`: `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`,
CSP `default-src 'none'; frame-ancestors 'none'`, HSTS `max-age=63072000;
includeSubDomains` при HTTPS. `Server` убран (`AddServerHeader = false`).

### Rate limiting: два независимых слоя

**Слой A — in-memory, для входа.** `Api/Auth/AuthRateLimiter.cs`,
`MemoryAuthRateLimiter` на `IMemoryCache`, singleton.

| Метод | Ключ | Лимит | Окно |
|---|---|---|---|
| `CheckLogin(ip, email)` | `rl:login:{ip}:{email}` | 5 | 15 минут |
| `CheckLoginCodeRequest(ip)` (WIP) | `rl:login-code:{ip}` | 10 | 1 час |

Вынесен отдельно потому, что middleware-лимитер не видит email — тело запроса
на том этапе ещё не прочитано (комментарий `:9-11`).

**Слой B — встроенный `RateLimiter`.** `Api/Security/RateLimitingSetup.cs`,
секция конфигурации `RateLimit`, все — `FixedWindowRateLimiter`, `QueueLimit = 0`,
429 + `Retry-After` + запись в журнал безопасности.

| Политика | Ключ | Лимит по умолчанию | Окно |
|---|---|---|---|
| глобальная | IP | 120 | 1 мин |
| `search` | IP | 60 | 1 мин |
| `profile-by-code` | IP | 20 | 1 мин |
| `sensitive-anon` | IP | 3 | 1 час |
| `create-listing` | user / IP | 10 | 1 час |
| `business-submit` | user / IP | 5 | 1 час |
| `contact` | user / IP | 30 / 10 | 1 час |
| `report` | user / IP | 20 / 5 | 1 час |
| `feedback` | user / IP | 20 / 5 | 1 час |

На auth-эндпоинтах: `SensitiveAnon` на `register` и `forgot-password`;
`reset-password` — намеренно без политики; `login`, `login-code`,
`login-code/verify` — только слой A плюс глобальный лимит.

**На `/api/me/*` атрибутов rate-limit нет вообще** — только глобальный лимит и
кулдауны внутри `VerificationService`.

**Слой C — доменные кулдауны в БД:** 60 с на повторную отправку кода
подтверждения и ссылки восстановления. Считаются по `CreatedAt` в таблице,
поэтому переживают рестарт.

---

## 10. Зависимости от пользователя

Всё завязано на `Guid` — других типов ключей нет. Разделение на «настоящие FK» и
«просто `uuid`» принципиально: при выносе `users` в отдельную базу первые
придётся разрывать явно, вторые сломаются молча (или не сломаются вовсе).

### Настоящие FK на `users` — 13 связей

| Сущность | Таблица | FK-свойство | OnDelete |
|---|---|---|---|
| `Profile` | `profiles` | `UserId` (PK=FK) | **Cascade** |
| `BusinessProfile` | `business_profiles` | `UserId` (PK=FK) | **Cascade** |
| `RefreshToken` | `refresh_tokens` | `UserId` | **Cascade** |
| `VerificationCode` | `verification_codes` | `UserId` | **Cascade** |
| `PasswordResetToken` | `password_reset_tokens` | `UserId` | **Cascade** |
| `Favorite` | `favorites` | `UserId` (часть PK) | **Cascade** |
| `SavedSearch` | `saved_searches` | `UserId` | **Cascade** |
| `Listing` | `listings` | `OwnerId` | **Restrict** |
| `Review` | `reviews` | `AuthorId` | **Restrict** |
| `Review` | `reviews` | `TargetUserId` | **Restrict** |
| `Conversation` | `conversations` | `BuyerId` | **Restrict** |
| `Conversation` | `conversations` | `SellerId` | **Restrict** |
| `Message` | `messages` | `SenderId` | **Restrict** |

Логика разделения: Cascade — то, что принадлежит аккаунту; Restrict — то, что
должно пережить его удаление. Удаление аккаунта реализовано мягко
(анонимизация + архивация объявлений), комментарий `UserConfiguration.cs:51-52`.

### Ссылки на пользователя без FK

| Сущность | Таблица | Свойство | Тип |
|---|---|---|---|
| `ContactReveal` | `contact_reveals` | `ViewerUserId` | `Guid?` |
| `ModerationLog` | `moderation_logs` | `ActorId` | `Guid` |
| `ModerationLog` | `moderation_logs` | `TargetId` (при `TargetType`=user/business) | `Guid` |
| `Report` | `reports` | `ReporterId` | `Guid?` |
| `Report` | `reports` | `ResolvedByUserId` | `Guid?` |
| `Report` | `reports` | `AssignedToUserId` | `Guid?` |
| `Report` | `reports` | `TargetId` (при `TargetType`=User) | `Guid` |
| `Review` | `reviews` | `HiddenByUserId` | `Guid?` |
| `BlockedCard` | `blocked_cards` | `CreatedByUserId` | `Guid` |
| `Listing` | `listings` | `ReviewAssigneeId` | `Guid?` |
| `BusinessProfile` | `business_profiles` | `ReviewedByUserId` | `Guid?` |

Похоже, это сознательный выбор (append-only журналы должны переживать удаление
аккаунта), но в коде комментария об этом решении нет — в отличие от
`Review.TargetUserId`, где FK заведён явно и объяснён.

### Сущности без связи с пользователем

`listing_images`, `subcategories`, `search_misses`, `outbox_messages`,
`channel_post_queue`, `link_clicks`, `feedback_messages`.

### Миграции

Каталог `Infrastructure/Persistence/Migrations/` — 31 миграция плюс
`AppDbContextModelSnapshot.cs`. Миграции не накатываются при старте приложения;
DDL — отдельный шаг деплоя `scripts/migrate.sh` под владельцем схемы,
приложение ходит в БД под ролью с правами только на DML.

Ключевые для auth: `InitialSchema` (создание `users`), `AddAuth`
(`refresh_tokens`), `EmailVerificationAndGeneralize` (обобщение кодов
подтверждения), `AddPasswordResetTokens`, `AddUserPublicCode`,
`AddRefreshTokenSessions`, `ModerationWorkspaceReads` (индекс по
`CreatedByIpHash`), `ModerationSanctions` (предупреждения).

---

## 11. Список auth-эндпоинтов

### `/api/auth/*` (`Api/Controllers/AuthController.cs`)

| Метод и путь | Вход | Выход | Доступ | Лимит |
|---|---|---|---|---|
| `POST /api/auth/register` | `RegisterRequest` | `MessageResponse` 200 | аноним | `SensitiveAnon` 3/час на IP |
| `POST /api/auth/login` | `LoginRequest` | `AuthResponse` 200 / 401 | аноним | ручной 5/15 мин на (IP, email) |
| `POST /api/auth/login-code` **(WIP)** | `LoginCodeRequest` | `MessageResponse` 200 | аноним | ручной 10/час на IP |
| `POST /api/auth/login-code/verify` **(WIP)** | `LoginByCodeRequest` | `AuthResponse` 200 / 401 | аноним | ручной, общий с `login` |
| `POST /api/auth/refresh` | `RefreshRequest` | `AuthResponse` 200 / 401 | аноним | глобальный |
| `POST /api/auth/logout` | `LogoutRequest` | 204 | `[Authorize]` | глобальный |
| `POST /api/auth/logout-all` | — | 204 | `[Authorize]` | глобальный |
| `POST /api/auth/change-password` | `ChangePasswordRequest` | 204 / 400 | `[Authorize]` | глобальный |
| `POST /api/auth/forgot-password` | `ForgotPasswordRequest` | `MessageResponse` 200 | аноним | `SensitiveAnon` |
| `POST /api/auth/reset-password` | `ResetPasswordRequest` | 204 / 410 / 400 | аноним | намеренно без политики |

### `/api/me/*`

| Метод и путь | Вход | Выход | Доступ |
|---|---|---|---|
| `GET /api/me` | — | `MeResponse` | `[Authorize]` |
| `PATCH /api/me` | `UpdateMeRequest` | `MeResponse` | `[Authorize]` |
| `POST /api/me/avatar` | `IFormFile` | `AvatarResponse` | `[Authorize]` |
| `DELETE /api/me/avatar` | — | 204 | `[Authorize]` |
| `GET /api/me/sessions` | — | `SessionResponse[]` | `[Authorize]` |
| `DELETE /api/me/sessions/{id}` | — | 204 / 404 | `[Authorize]` |
| `DELETE /api/me` | — | 204 | `[Authorize]` |
| `POST /api/me/email/send-code` | — | `SendCodeResponse` | `[Authorize]` |
| `POST /api/me/email/verify` | `VerifyCodeRequest` | `MessageResponse` | `[Authorize]` |
| `POST /api/me/phone/send-code` | — | `SendCodeResponse` | `[Authorize]` |
| `POST /api/me/phone/verify` | `VerifyCodeRequest` | `MessageResponse` | `[Authorize]` |

DTO — `Api/Contracts/AuthDtos.cs` и `Api/Contracts/ProfileDtos.cs`.

`UserResponse` (в теле входа/обновления): `Id`, `PublicCode`, `Email`,
`DisplayName`, `City`, `PhoneE164`, `PhoneVerified`, `CreatedAt`,
`EmailVerified`. **Роли в нём нет** — она только в токене и в `MeResponse`.

---

## Что переносится в Genesis ID, а что остаётся на площадке

Это оценка на основе прочитанного кода, а не утверждённое решение.

### Переносится (домен идентичности)

| Что | Комментарий |
|---|---|
| `users` целиком? | Не целиком — см. ниже про счётчики |
| `Email`, `PasswordHash`, `PublicCode`, `Role`, `SecurityStamp` | ядро идентичности |
| `PhoneE164`, `PhoneVerified`, `EmailVerified` | подтверждение контактов |
| `IsBanned`, `BannedUntil`, `IsDeleted` | состояние аккаунта |
| `refresh_tokens` | сессии и устройства |
| `verification_codes` | коды подтверждения и входа |
| `password_reset_tokens` | восстановление пароля |
| Все `/api/auth/*` | выпуск, ротация, отзыв |
| `/api/me/sessions*`, `/api/me/email/*`, `/api/me/phone/*` | управление своей идентичностью |
| Rate-limit входа, анти-перечисление | неотделимы от ручек входа |
| Отправка auth-писем и шаблоны | вместе с ручками |

### Остаётся на площадке (торговый домен)

| Что | Почему |
|---|---|
| `profiles` | отображаемое имя, город, аватар, мессенджеры — это профиль продавца, не идентичность |
| `business_profiles` | реквизиты и проверка бизнеса — правила конкретной площадки |
| `AverageRating`, `ReviewsCount`, `ApprovedListingsCount`, `LastRejectedAt` | репутация внутри площадки; поддерживаются триггерами БД рядом с `listings`/`reviews` |
| `WarningsCount`, `LastWarnedAt` | санкции модерации площадки |
| `moderation_logs`, `reports`, `blocked_cards` | инструменты модератора |
| Весь торговый домен | `listings`, `favorites`, `saved_searches`, `reviews`, `conversations`, `messages`, `contact_reveals` |

### Что придётся решать при разделении

- **13 FK на `users` придётся разорвать** либо держать в базе площадки таблицу-реплику
  `users` (только идентификатор + минимум полей) и синхронизировать её. Второй
  вариант сохраняет целостность и триггеры, но требует канала синхронизации.
- **Счётчики доверия и предупреждения сейчас физически лежат в `users`**
  и пишутся триггерами, которые ссылаются на `listings`/`reviews`. При выносе
  `users` эти колонки надо переносить в таблицу площадки (например,
  в `profiles`), иначе триггеры перестанут работать — это отдельная миграция,
  не «просто перенос».
- **Общий симметричный HS256-секрет не масштабируется на несколько сервисов:**
  любой сервис, знающий ключ, может выпустить токен от имени кого угодно.
  Для экосистемы нужен асимметричный ключ (RS256/ES256) и публикация JWKS,
  чтобы площадки только проверяли подпись, а выпускал один Genesis ID.
- **Роль сейчас глобальная.** `UserRole` — одно поле на весь аккаунт. В
  экосистеме «модератор Маркета» не должен автоматически быть модератором другого
  продукта; роль надо делать per-application.
- **`SecurityStamp` — механизм мгновенного отзыва.** Сегодня площадка сверяет его
  с БД при каждом запросе (с кэшем 30 с). После разделения площадка не будет
  иметь доступа к таблице `users` — нужен либо эндпоинт проверки,
  либо короткий срок жизни access-токена без сверки, либо канал уведомлений об
  отзыве. Это архитектурное решение, а не деталь.
- **Публичный код** — кандидат на общеэкосистемный идентификатор. Но пул в
  90 000 придётся расширять: подбор деградирует задолго до формального
  исчерпания (см. комментарий в `PublicCodeGenerator.cs:43-50`).
- **Смены email нет** — при выносе её всё равно придётся написать; логичнее
  сразу в Genesis ID.

---

## Риски и слабые места

### Высокий приоритет

**1. Коды подтверждения и входа хэшируются голым SHA-256 без ключа.**
`VerificationService.cs:139`. Код — 6 цифр, то есть 10⁶ вариантов; SHA-256 без
соли и без секретного ключа перебирается по дампу базы мгновенно, радужная
таблица на всё пространство строится за секунды. Это касается и кодов
подтверждения почты, и кодов входа (WIP), то есть при утечке дампа активные коды
дают прямой вход в чужие аккаунты.

Это **прямое расхождение с собственным техзаданием**: `docs/email/OTP-BRIEF.md:23-26`
явно требует HMAC-SHA256 с серверным ключом из конфигурации и объясняет, почему
(«энтропии 6 цифр мало, но секретный ключ HMAC закрывает перебор по украденному
дампу»). Ключ для этого в проекте уже есть — `Security:IpHashKey` используется
аналогичным образом в `IpHasher`. Смягчающее обстоятельство: TTL кода 5 минут,
так что окно эксплуатации узкое.

**2. Общий симметричный ключ подписи как модель доверия для нескольких сервисов.**
Сегодня это не проблема (сервис один), но именно на этом строится Genesis ID.
HS256 означает, что проверяющий и выпускающий имеют один и тот же секрет:
любая площадка экосистемы сможет выпустить токен от имени администратора.
Решать до, а не после запуска.

### Средний приоритет

**3. Rate-limit входа живёт в памяти процесса.** `MemoryAuthRateLimiter` на
`IMemoryCache`: рестарт контейнера обнуляет счётчики, а при двух инстансах
каждый считает свои. То же ограничение зафиксировано в CLAUDE.md для квоты
раскрытия контактов — там его уже решили журналом в БД, здесь нет.

**4. Ключ лимита входа включает email.** `rl:login:{ip}:{email}` — перебор
паролей к одному адресу ограничен пятью попытками, но перебор адресов с одного
IP (credential stuffing) упирается только в глобальный лимит 120 запросов в
минуту.

**5. Забаненный получает refresh-токен.** `Login` и `LoginByCode` не проверяют
`IsBanned`/`IsDeleted` — проверка срабатывает позже, в `SecurityStampValidator`,
при первом использовании access-токена. Access действительно не сработает, но
refresh-токен на 30 дней забаненному выдаётся и лежит в базе как активная сессия.

**6. `AllowCredentials()` в CORS при полностью bearer-модели.** Сервер не ставит
и не читает cookie; послабление ничего не даёт и расширяет поверхность.

**7. `JWT_EXPIRES_DAYS` в `.env` ничего не задаёт.** Переменная объявлена в
`.env.example`, `.env`, `.env.dev`, `.env.dev.example` (значение 7), но нигде не
маппится в compose и не читается кодом. Реальные сроки — `Jwt:AccessTokenMinutes`
15 и `Jwt:RefreshTokenDays` 30 из `appsettings.json`, и **ни один из них не
пробрасывается через env**, то есть изменить срок жизни токенов без пересборки
образа сейчас нельзя. Читающий `.env` сделает вывод, что refresh живёт 7 дней
вместо фактических 30.

**8. На `/api/me/*` нет ни одного атрибута rate-limit.** Отправка кодов защищена
только кулдауном в 60 с на пользователя и глобальным лимитом на IP.

### Низкий приоритет

**9. Пул публичных кодов — 90 000**, и подбор деградирует задолго до
исчерпания: при 45 000 аккаунтов одна регистрация из тысячи упирается в 10
попыток, при 72 000 — каждая десятая. Разрядность придётся расширять.

**10. `PasswordHash varchar(200)`** — запас есть, но жёсткий предел на колонке
хэша осложнит переход на алгоритм с более длинным представлением (например,
Argon2id в формате PHC).

**11. Расхождение оформления публичного кода на фронтенде:** `GEN-XXXXX` в
профиле и магазине, `#XXXXX` на модераторских экранах. Плюс сервер префикс не
снимает нигде, кроме поиска модератора, — код с префиксом, посланный в
`/api/users/by-code/{code}`, молча даст 404.

**12. Политики `Admin` и `NotBanned` объявлены, но не применены нигде** —
модератор и админ фактически равноправны.

**13. `EmailVerified` и `IsDeleted` — без `DEFAULT false` в БД**, в отличие от
соседних `PhoneVerified` и `IsBanned`. Функционально безвредно (C#-дефолт), но
асимметрия заметна при прямых вставках в SQL.
То же с `WarningsCount`: `defaultValue: 0` задан в миграции, но не в модели.

**14. Номера строк в `docs/authorization-matrix.md` частично устарели** — по
существу документ верен, ссылки стоит перепроверить при следующем обновлении.

**15. Расхождение `docs/email/OTP-BRIEF.md` с реализацией по транспорту:** бриф
описывает MailKit и `smtp.gmail.com`, фактически используется `System.Net.Mail`.
Само по себе безвредно, но бриф читается как описание существующего.

---

## Открытые вопросы

На эти вопросы по коду ответа нет — их нужно решить при проектировании.

1. **Сколько приложений будет в экосистеме и нужен ли стандартный протокол?**
   Своя схема (bearer + refresh в теле) работает для одного фронтенда. Для
   нескольких продуктов и возможных сторонних интеграций обычно берут OIDC/OAuth2
   с готовой библиотекой. Это решение определяет почти всё остальное.

2. **На каком домене будет жить Genesis ID и допустимы ли cookie на
   `.genesis-hq.com`?** Сегодня серверных cookie нет вовсе, и вся схема
   (httpOnly `gm_refresh` + CSRF double-submit) построена внутри Next.js. При
   переходе на общую сессию для поддоменов её придётся переносить на сервер —
   это фактически новая модель, а не правка.

3. **Остаётся ли 5-значный публичный код общеэкосистемным идентификатором или
   становится per-app?** От этого зависит, расширять ли разрядность и надо ли
   мигрировать существующие коды.

4. **Переносятся ли роли в Genesis ID или каждая площадка ведёт свои?**
   Сейчас роль глобальная, и назначается она только прямой правкой в БД.

5. **Будет ли у Genesis ID отдельная база?** Если да — 13 FK на `users` надо
   разрывать, а счётчики доверия и предупреждения переносить из `users` в
   таблицы площадки (вместе с триггерами). Если общая схема — вопрос снимается,
   но теряется изоляция.

6. **Что делать со ссылками без FK** (`moderation_logs.ActorId`,
   `reports.ReporterId` и остальные девять) после разделения? Формально они
   переживут вынос молча, но перестанут разрешаться в пользователя без
   обращения к Genesis ID.

7. **Как площадка узнаёт об отзыве сессии и бане после разделения?**
   Сегодня это сверка `SecurityStamp` с БД на каждом запросе. Варианты:
   эндпоинт интроспекции, вебхук, короткий TTL без сверки — у каждого своя цена.

8. **Нужен ли обратный путь Genesis ID → площадка** для бана и удаления
   аккаунта (чтобы объявления забаненного скрывались)?

9. **Политика retention для `IpHash` и журналов после разделения.** Сейчас
   `JournalRetention` — 90 дней, ключ `Security:IpHashKey` один на сервис.
   При двух сервисах ключ либо общий (тогда HMAC сравним между ними — это и
   возможность, и риск), либо разный (тогда сетевой сигнал в досье модератора
   перестанет работать).

10. **Переживут ли хэши паролей перенос на практике?** Теоретический ответ в
    разделе 4 положительный, но `EnhancedHashPassword` — нестандартный вариант,
    и это надо подтвердить экспериментом на копии данных до миграции.

---

*Собрано read-only 2026-09-24. Изменений в коде, конфигурации, миграциях и базе
не производилось.*
