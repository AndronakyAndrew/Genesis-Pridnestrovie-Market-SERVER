# Матрица авторизации — Genesis Market

Кто что может делать и **где именно на сервере это обеспечено** (файл:строка).
Собственный JWT + `Microsoft.AspNetCore.Authorization` (ASP.NET Identity не используется).

## Роли

`User` (по умолчанию при регистрации), `Moderator`, `Admin`. Роль берётся **только**
из claim `role` токена (кладётся в `Api/Auth/JwtTokenService.cs`), никогда из тела/заголовка/query.

## Глобальные правила (действуют на всё)

| Правило | Где обеспечено |
|---|---|
| Всё защищено по умолчанию: забытый `[Authorize]` не открывает эндпоинт (`FallbackPolicy = RequireAuthenticatedUser`) | `Api/Auth/AuthServiceCollectionExtensions.cs:111` |
| Политика `Moderator` (role ∈ {Moderator, Admin}) | `…/AuthServiceCollectionExtensions.cs:99` |
| Политика `Admin` (role = Admin) | `…/AuthServiceCollectionExtensions.cs:101` |
| Политика `NotBanned` (= аутентифицирован; забаненные не проходят валидацию токена) | `…/AuthServiceCollectionExtensions.cs:104` + `Api/Auth/SecurityStampValidator.cs` |
| Проверка владения `ResourceOwner`: **владелец ИЛИ модератор/админ** | `Api/Auth/ResourceOwnerAuthorization.cs:18` (политика — `…Extensions.cs:106`) |
| Текущий пользователь читается только через `ICurrentUser` | `Api/Auth/CurrentUser.cs` |
| «Нет объекта» vs «объект чужой»: приватные ресурсы → **404** в обоих случаях; публичные (каталог) → **403** для чужого | см. `ListingsController.Delete` |

## Матрица «ресурс × операция»

Обозначения: 🟢 разрешено · 🔒 нужна аутентификация · 👤 только владелец · 🛡 модератор/админ · 🌐 публично (в т.ч. аноним).

### Listing (объявление)

| Операция | Аноним | User (не владелец) | Владелец | Moderator/Admin | Где обеспечено |
|---|---|---|---|---|---|
| Читать каталог `GET /api/listings` | 🌐 200 | 🌐 200 | 🌐 200 | 🌐 200 | `ListingsController.cs:24` `[AllowAnonymous]` |
| Читать карточку `GET /api/listings/{id}` | 🌐 200 | 🌐 200 | 🌐 200 | 🌐 200 | `ListingsController.cs:38` `[AllowAnonymous]` |
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

### Подтверждение контактов (свой профиль)

| Операция | Доступ | Где обеспечено |
|---|---|---|
| `POST /api/me/phone/{send-code,verify}` | 🔒 свой | `PhoneVerificationController.cs:13` `[Authorize]` |
| `POST /api/me/email/{send-code,verify}` | 🔒 свой | `EmailVerificationController.cs:13` `[Authorize]` |

### Инфраструктура

| Эндпоинт | Доступ | Где обеспечено |
|---|---|---|
| `GET /health/live`, `/health/ready` | 🌐 аноним | `Program.cs` (`.AllowAnonymous()` на MapHealthChecks) |

## Планируемое (эндпоинтов пока нет)

- **Модерация** — под политикой `Moderator`/`Admin` (уже зарегистрированы). Когда появится `ModerationController`, повесить `[Authorize(Policy = "Moderator")]`.
- **Review, SavedSearch** — приватные владельческие ресурсы: сущностям достаточно реализовать
  `IOwnedResource` (`Domain/Common/IOwnedResource.cs`) — тот же `ResourceOwnerHandler` начнёт их
  покрывать без нового кода. Для них «чужой» и «нет объекта» → **404** (существование не публично).

## Тесты

`tests/GenesisMarket.Tests/AuthorizationTests.cs`: на защищённый эндпоинт (удаление объявления) —
аноним → 401, чужой → 403, владелец → 204, модератор → 204; плюс гость может смотреть каталог и регистрироваться.
