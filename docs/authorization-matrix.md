# Матрица авторизации — Genesis Market

Кто что может делать и **где именно на сервере это обеспечено**.
Собственный JWT + `Microsoft.AspNetCore.Authorization` (ASP.NET Identity не используется).

## Роли

`User` → `Moderator` → `Admin` → `Owner`. Порядок — ранг: каждая следующая роль включает
права предыдущей (`Domain/Enums/Enums.cs`, `UserRole`).

- **Хранение — модель «пользователь + проект + роль»**: таблица `project_role_assignments`
  (`UserId`, `ProjectKey = 'market'`, `Role`). Строка есть только у служебных ролей; нет строки —
  `User`. Колонки `users.Role` больше нет. Чтение и запись — только через `IRoleStore`
  (`Api/Auth/Roles/RoleStore.cs`): при переносе ролей в Genesis Ops меняется реализация
  хранилища, а политики и правила смены ролей остаются как есть.
- При регистрации роль не назначается вовсе (`AuthController.Register`), поля роли в запросе нет.
- **Owner — ровно один на проект** (частичный уникальный индекс
  `ux_project_role_assignments_single_owner`). Назначается только CLI-командой `grant-role`.
- В токене роль — claim `role`, берётся из `IRoleStore` при выпуске (`JwtTokenService`), никогда
  из тела/заголовка/query. Актуальность держит `SecurityStamp`: смена роли его ротирует и отзывает
  все refresh-токены цели — токен с прежней ролью умирает сразу.

## Разрешения служебной зоны

Эндпоинты проверяют **разрешение**, а не имя роли: `[Authorize(Policy = Permissions.UsersBan)]`.
Карта «роль → разрешения» одна — `Api/Auth/Permissions.cs` (`RolePermissions`).

| Разрешение | Что даёт | Moderator | Admin | Owner |
|---|---|---|---|---|
| `listings.moderate` | Очередь, решения по объявлениям, назначение | ✅ | ✅ | ✅ |
| `reports.review` | Жалобы: список, взять, закрыть | ✅ | ✅ | ✅ |
| `reviews.moderate` | Скрытие отзывов | ✅ | ✅ | ✅ |
| `users.view` | Реестр, досье, журналируемый просмотр контактов | ✅ | ✅ | ✅ |
| `users.warn` | Предупреждение | ✅ | ✅ | ✅ |
| `cards.manage` | Чёрный список карт | ✅ | ✅ | ✅ |
| `business.review` | Заявки бизнеса | ✅ | ✅ | ✅ |
| `moderation.journal.read` | Журнал модерации, статистика | ✅ | ✅ | ✅ |
| `users.ban` | Блокировка и разблокировка | — | ✅ | ✅ |
| `catalog.manage` | Справочники: города, категории, подкатегории | — | ✅ | ✅ |
| `roles.manage` | Сотрудники: список, поиск по email, смена ролей | — | — | ✅ |
| `audit.read` | Журнал аудита и история ролей | — | — | ✅ |

**Разрешение действует только при пройденной 2FA** (claim `mfa`): `PermissionHandler`
(`Api/Auth/PermissionAuthorization.cs`). Сотрудник без 2FA получает 403 с `code: "mfa_setup_required"`.
То же вне служебных префиксов: `ICurrentUser.Can(...)` требует `mfa` для видимости неопубликованного,
сырого текста и удаления чужих объявлений (`ResourceOwnerHandler`, `ListingVisibility`).

### Правила поверх разрешений

| Правило | Где обеспечено |
|---|---|
| Нельзя решать и назначать на себя **своё** объявление (одиночно и пакетом) — Owner не исключение | `ModerationController.DecideAsync`, `ModerationQueueController.Assign/BulkAsync`, `Moderation/ConflictOfInterest.cs` |
| Нельзя брать и закрывать жалобу на себя, своё объявление, отзыв о себе или свой | `ModerationReportsController.Take`, `ModerationController.ResolveReport` |
| Нельзя скрыть отзыв о себе или свой | `ReviewsController.Hide` |
| Санкции — только к роли **строго ниже** своей: модератор не предупреждает модератора, админ не банит админа, владельца не трогает никто | `RolePermissions.Outranks`; `ModerationUsersController.Warn`, `AdminUsersController.Ban` |
| Owner не назначается через API; свою роль менять нельзя; роль владельца через API не меняется; заблокированного не повышают | `RoleManagementService.ChangeByOwnerAsync` |
| Смена роли сверяется с ролью из диалога подтверждения (`expectedRole`), иначе 409 | там же |

## Двухфакторная аутентификация (TOTP)

- RFC 6238 (HMAC-SHA1, 30 с, 6 цифр, допуск ±1 шаг), своя реализация — `Api/Auth/TwoFactor/Totp.cs`,
  проверена векторами RFC. Один шаг дважды не принимается (`LastUsedTimeStep`).
- Секрет — AES-256-GCM, ключ `Security:TotpKey` (32 байта base64) **только из окружения**; без него
  приложение не стартует. Резервные коды (10 шт.) — HMAC, одноразовые, гасятся условным UPDATE.
- Вход паролем или кодом из письма при включённой 2FA → **202** с одноразовым билетом (5 минут,
  5 попыток) → `POST /api/auth/2fa/verify` → токены с `mfa`.
- Отметка 2FA — свойство сессии (`refresh_tokens.MfaVerifiedAt`), переживает ротацию.
- Сотрудник не может отключить 2FA (`POST /api/me/2fa/disable` → 403). Включение 2FA завершает прочие
  сессии и выдаёт текущей новый access-токен с `mfa`.
- **Короткая служебная сессия**: refresh-токен сотрудника живёт не дольше `StaffSession:AbsoluteHours`
  (12 ч) от входа и умирает после `StaffSession:IdleMinutes` (60 мин) простоя — `RefreshTokenService`.

## Глобальные правила (действуют на всё)

| Правило | Где обеспечено |
|---|---|
| Всё защищено по умолчанию: забытый `[Authorize]` не открывает эндпоинт (`FallbackPolicy = RequireAuthenticatedUser`) | `Api/Auth/AuthServiceCollectionExtensions.cs` |
| Политики-разрешения (`Permissions.All`) + обязательный claim `mfa` | `…/AuthServiceCollectionExtensions.cs`, `Api/Auth/PermissionAuthorization.cs` |
| Политика `NotBanned` (= аутентифицирован; забаненные не проходят валидацию токена) | `…/AuthServiceCollectionExtensions.cs` + `Api/Auth/SecurityStampValidator.cs` |
| Проверка владения `ResourceOwner`: **владелец ИЛИ сотрудник с `listings.moderate` и 2FA** | `Api/Auth/ResourceOwnerAuthorization.cs` |
| Текущий пользователь читается только через `ICurrentUser` | `Api/Auth/CurrentUser.cs` |
| «Нет объекта» vs «объект чужой»: приватные ресурсы → **404** в обоих случаях; публичные (каталог) → **403** для чужого | см. `ListingsController.Delete` |

## Матрица «ресурс × операция»

Обозначения: 🟢 разрешено · 🔒 нужна аутентификация · 👤 только владелец · 🛡 модератор/админ · 🌐 публично (в т.ч. аноним).

### Listing (объявление)

| Операция | Аноним | User (не владелец) | Владелец | Moderator/Admin | Где обеспечено |
|---|---|---|---|---|---|
| Читать каталог `GET /api/listings` | 🌐 200 | 🌐 200 | 🌐 200 | 🌐 200 | `ListingsController.cs:24` `[AllowAnonymous]` |
| Читать карточку `GET /api/listings/{id}` (опубликованное, в т.ч. Sold/Archived) | 🌐 200 | 🌐 200 | 🌐 200 | 🌐 200 | `ListingsController.cs` `[AllowAnonymous]` |
| Читать карточку неопубликованного (Draft/PendingReview/Rejected) | **404** | **404** | 👤 200 | 🛡 200 | `ListingsController.CanSee` (+ то же для `by-slug`) |
| Видеть контакты в тексте описания | редактируется | редактируется | 👤 как написал | 🛡 как написал | `ListingsController.Map` → `ListingContentRisk.RedactContacts` |
| Раскрыть контакты `GET /api/listings/{id}/contact` | 🌐 200, 10/час на IP | 🔒 200 при подтверждённой почте, 30/час на аккаунт; иначе **403** | то же | то же | `ListingsController.GetContact` + `ContactRevealService.QuotaRetryAfterAsync` |
| Создать `POST /api/listings` | 401 | 🔒 201 (если контакт подтверждён) | 🔒 201 | 🔒 201 | `ListingsController.cs:51` `[Authorize]` + `:68` `IPublishingPolicy` |
| Удалить `DELETE /api/listings/{id}` | 401 | **403** | 👤 204 | 🛡 204 | `ListingsController.cs:107` `[Authorize]` + `:116` `AuthorizeAsync(ResourceOwner)` |
| Изменить (PATCH) | — | — | 👤 | 🛡 | *эндпоинта пока нет; при добавлении — `ResourceOwner`* |

### Auth / сессия

| Операция | Доступ | Где обеспечено |
|---|---|---|
| `POST /api/auth/register` | 🌐 аноним | `AuthController.cs:29` `[AllowAnonymous]` |
| `POST /api/auth/login` | 🌐 аноним | `AuthController.cs:75` `[AllowAnonymous]` |
| `POST /api/auth/refresh` | 🌐 аноним | `AuthController.cs:107` `[AllowAnonymous]` |
| `POST /api/auth/logout` | 🔒 свой токен | `AuthController.cs:130` `[Authorize]` |
| `POST /api/auth/logout-all` | 🔒 свой аккаунт | `AuthController.cs:139` `[Authorize]` |
| `POST /api/auth/change-password` | 🔒 свой аккаунт | `AuthController.cs:156` `[Authorize]` |
| `POST /api/auth/2fa/verify` | 🌐 аноним с билетом из ответа 202 | `AuthController.VerifySecondFactor` + `MfaChallengeService` |
| `GET /api/me/2fa`, `POST /api/me/2fa/{setup,enable,recovery-codes,disable}` | 🔒 свой аккаунт; ручки с кодом — лимит 5/15 мин | `TwoFactorController` |

### Подтверждение контактов (свой профиль)

| Операция | Доступ | Где обеспечено |
|---|---|---|
| `POST /api/me/phone/{send-code,verify}` | 🔒 свой | `PhoneVerificationController.cs:13` `[Authorize]` |
| `POST /api/me/email/{send-code,verify}` | 🔒 свой | `EmailVerificationController.cs:13` `[Authorize]` |

### Инфраструктура

| Эндпоинт | Доступ | Где обеспечено |
|---|---|---|
| `GET /health/live`, `/health/ready` | 🌐 аноним | `Program.cs` (`.AllowAnonymous()` на MapHealthChecks) |

### Модерация (`/api/moderation/*`)

Каждая ручка — под своим разрешением (таблица выше); аноним → 401, роль без разрешения → 403,
сотрудник без 2FA → 403 `mfa_setup_required`. Каждое действие пишется в `moderation_logs` в той же
транзакции (`Api/Moderation/ModerationAudit.cs`) и зеркалом — в общий журнал `audit_events`.

| Эндпоинт | Разрешение | Что | Где обеспечено |
|---|---|---|---|
| `GET queue`, `GET listings/{id}`, `POST listings/{id}/{approve,reject,revise}` | `listings.moderate` | Очередь и решения (через `IListingDecisions`), не по своим объявлениям | `ModerationController` |
| `GET listings`, `GET listings/counts`, `POST listings/{id}/{assign,unassign}`, `POST listings/bulk/{approve,reject,revise,assign}` | `listings.moderate` | Таблица очереди, назначение, пакеты ≤ 50 одной транзакцией; свои объявления пакет отбрасывает | `ModerationQueueController` |
| `POST reports/{id}/resolve` | `reports.review` | Закрытие жалобы (не касающейся себя) | `ModerationController` |
| `GET stats` | `moderation.journal.read` | Счётчики дашборда | `ModerationController` |
| `GET users/{id}`, `GET users/by-code/{code}` | `users.view` | **Контакты** (email/телефон). Единственные ручки с контактами; каждый вызов журналируется | `ModerationController.UserContactsAsync` |
| `GET logs`, `GET logs/summary`, `GET logs/actors` | `moderation.journal.read` | Журнал модерации (только чтение) | `ModerationJournalController` |
| `GET users`, `GET users/summary`, `GET users/{id}/dossier` | `users.view` | Реестр и досье **без контактов**; email в поиске не участвует | `ModerationUsersController` |
| `POST users/{id}/warn` | `users.view` + `users.warn` | Предупреждение — не себе и только роли ниже своей | `ModerationUsersController.Warn` |
| `GET blocked-cards`, `POST blocked-cards`, `DELETE blocked-cards/{id}` | `cards.manage` | Чёрный список карт: номер → HMAC + последние 4 цифры | `ModerationBlockedCardsController` |
| `GET reports`, `GET reports/summary`, `GET reports/{id}`, `POST reports/{id}/take` | `reports.review` | Экран жалоб; «взять» — условный UPDATE, чужую только `?force=true`; касающуюся себя — 403 | `ModerationReportsController` |
| `GET business`, `POST business/{userId}/{approve,reject}` | `business.review` | Заявки бизнеса; решение — compare-and-set по `submittedAt` | `BusinessModerationController` |
| `POST /api/reviews/{id}/hide` | `reviews.moderate` | Скрытие отзыва (не о себе и не своего) | `ReviewsController.Hide` |

Сетевой сигнал в досье — совпадение HMAC адреса входа (`refresh_tokens.CreatedByIpHash`);
сырой IP не хранится и модератору не показывается, только два октета (`IpPrefix`).

### Служебная зона уровня Admin/Owner (`/api/admin/*`)

| Эндпоинт | Разрешение | Что | Где обеспечено |
|---|---|---|---|
| `POST users/{id}/ban`, `POST users/{id}/unban` | `users.ban` | Блокировка: SecurityStamp, архив объявлений, отзыв сессий; журнал «было → стало»; только роли ниже своей | `AdminUsersController` |
| `POST catalog/{cities,categories}`, `PATCH catalog/{cities,categories}/{code}` | `catalog.manage` | Города и категории: код неизменяем, удаления нет — только выключение | `AdminCatalogController` |
| `POST catalog/subcategories`, `PATCH catalog/subcategories/{id}` | `catalog.manage` | Подкатегории | `AdminCatalogController` |
| `GET staff`, `GET staff/lookup?email=`, `PUT staff/{userId}/role` | `roles.manage` | Сотрудники; поиск по email журналируется (адрес в журнал не пишется) | `AdminStaffController` |
| `GET audit`, `GET audit/role-changes` | `audit.read` | Журнал аудита и история ролей (только чтение) | `AdminAuditController` |

Публично: `GET /api/catalog/references` — все справочники одним ответом, с флагом `isActive`
(`CatalogReferencesController`). Коды на входе регистронезависимы, в БД и в ответах — канонические.

## Журнал действий

- `audit_events` — кто (`ActorId` либо `ActorKind = cli` + «пользователь ОС@машина»), когда, действие,
  объект, `OldValue`/`NewValue` (jsonb), причина, `RequestId`, HMAC адреса. Пишется **в той же
  транзакции**, что и действие (`Api/Audit/AuditLog.cs`).
- `role_changes` — отдельная история ролей (старая и новая роль, источник `ui`/`cli`) с парной записью
  в `audit_events`.
- Обе таблицы — только на добавление: UPDATE и DELETE отклоняет триггер `journal_append_only`
  (миграция `StaffRolesAuditTwoFactor`). Единственное разрешённое изменение — снятие `IpHash`
  по сроку хранения (`JournalRetentionService`, 90 дней).

## CLI (аварийные случаи)

Правила и журнал — те же, что у интерфейса (`Api/Cli/AdminCli.cs` → `RoleManagementService`).
Поднимается только DI с базой — без веб-сервера, Quartz и MinIO.

```bash
# dev: внутри контейнера API (окружение — из .env.dev)
docker exec -it genesis-api-dev dotnet run --no-build \
  --project src/GenesisMarket.Api/GenesisMarket.Api.csproj --no-launch-profile -- \
  grant-role <email> <User|Moderator|Admin|Owner> [--transfer] [--reason "…"] [--yes]

docker exec -it genesis-api-dev dotnet run --no-build \
  --project src/GenesisMarket.Api/GenesisMarket.Api.csproj --no-launch-profile -- \
  reset-2fa <email> [--reason "…"] [--yes]
```

Owner, когда владелец уже есть, — только с `--transfer` (прежний становится Admin в той же
транзакции). Без терминала команда требует `--yes`. Код выхода: 0 — готово, 1 — отказ, 2 — синтаксис.

## Планируемое (эндпоинтов пока нет)

- **Review, SavedSearch** — приватные владельческие ресурсы: сущностям достаточно реализовать
  `IOwnedResource` (`Domain/Common/IOwnedResource.cs`) — тот же `ResourceOwnerHandler` начнёт их
  покрывать без нового кода. Для них «чужой» и «нет объекта» → **404** (существование не публично).

## Тесты

- `StaffAccessMatrixTests` — служебные эндпоинты берутся **из таблицы маршрутов** (всё под
  `/api/moderation`, `/api/admin` и служебные ручки вне префиксов), новый эндпоинт попадает в
  проверку сам: у каждого есть политика-разрешение; аноним → 401; каждая роль ниже требуемой → 403,
  требуемая и выше → доступ; сотрудник без 2FA → 403 (`mfa_setup_required` там, где роль достаточна).
- `StaffRolesTests` — смена ролей владельцем (журнал, отзыв сессий, запреты, 409 на устаревшей роли),
  конфликт интересов, иерархия санкций, неизменяемость журналов на уровне БД, CLI `grant-role`/`reset-2fa`.
- `TwoFactorTests` — векторы RFC 6238, включение, вход в два шага, повтор кода, резервные коды,
  лимит попыток на билет, короткая служебная сессия.
- `CatalogReferencesTests` — справочники в БД, регистр кодов, выключенные значения, журнал.
- `AuthorizationTests` — удаление объявления: аноним → 401, чужой → 403, владелец → 204,
  модератор → 204; гость смотрит каталог и регистрируется.
