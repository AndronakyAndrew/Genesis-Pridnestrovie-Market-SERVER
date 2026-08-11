# Genesis Market — Server

Бэкенд маркетплейса объявлений ПМР (аналог Avito/OLX, локальный: города ПМР,
рубли ПМР, интерфейс на русском).

> Этот файл — точка входа для нового разработчика. Правила кода и архитектурные
> решения — в [`CLAUDE.md`](./CLAUDE.md) (не дублируются здесь). Ниже — как
> запустить проект, чего нельзя ломать, и **журнал фич** (что и почему сделано).

---

## Стек

- ASP.NET Core, **.NET 10 LTS**, EF Core 10 + Npgsql
- **PostgreSQL 17** (native enum-типы), **MinIO** (S3-совместимое хранилище фото)
- Аутентификация — своя: JWT + BCrypt (ASP.NET Identity **не** используется)
- Docker Compose: `api`, `postgres`, `minio`, `adminer`

## Структура решения

```
SERVER/
  src/
    GenesisMarket.Domain/          — сущности, enum-ы. БЕЗ внешних зависимостей.
    GenesisMarket.Infrastructure/  — DbContext, конфигурации EF, миграции, MinIO
    GenesisMarket.Api/             — контроллеры, DTO, middleware, Program.cs
  tests/GenesisMarket.Tests/       — интеграционные тесты
  docker-compose.yml               — api + postgres + minio + adminer
```

Зависимости направлены внутрь: `Api → Infrastructure → Domain`.

---

## Как запустить локально

Нужны: Docker + Docker Compose, .NET 10 SDK, `dotnet-ef` (`dotnet tool install --global dotnet-ef`).

```bash
cd SERVER

# 1. Переменные окружения (файл в .gitignore, значения — dev-заглушки).
cp .env.example .env            # затем заполнить (см. пример ниже)

# 2. Поднять инфраструктуру и API.
docker compose up -d --build

# 3. Бакет MinIO создаётся автоматически при старте API (MinioBucketInitializer,
#    имя — из Minio:Bucket). Ручной шаг больше не нужен.

# 4. Применить миграции к БД (design-time фабрика подключается к localhost:5434).
dotnet ef database update -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api
```

**Порты на хосте** (5432/5433/8080 заняты другими проектами — поэтому нестандартные):

| Сервис | Хост | Внутри сети |
|--------|------|-------------|
| API    | http://localhost:8090 | `api:8080` |
| PostgreSQL | `localhost:5434` | `postgres:5432` |
| Adminer (GUI к БД) | http://localhost:8081 | — |
| MinIO | не публикуется | `minio:9000` |

**Проверка, что всё живо:**

```bash
curl http://localhost:8090/health/live      # 200 — процесс жив
curl http://localhost:8090/health/ready     # Healthy — postgres + minio доступны
```

Остановить: `docker compose down` (данные в volume сохраняются),
со сбросом БД и хранилища: `docker compose down -v`.

### Работа с миграциями

```bash
dotnet ef migrations add <Имя> -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api -o Persistence/Migrations
dotnet ef migrations script -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api   # посмотреть SQL до применения
dotnet ef database update  -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api
```

Миграции кладутся в `Persistence/Migrations` (флаг `-o` обязателен) и коммитятся в репозиторий.

---

## Инварианты — что НЕЛЬЗЯ ломать

1. **Схема БД меняется только миграцией EF Core.** Никакого ручного SQL по таблицам.
2. **Наружу и внутрь контроллеров — только DTO из `Contracts/`**, не сущности EF. Маппинг руками.
3. **Каждый эндпоинт с id ресурса проверяет владельца на сервере** (`OwnerId != CurrentUserId → Forbid/404`). Не полагаться на фронт.
4. **Enum-ы — native-типы PostgreSQL** (`category`, `city`, `condition`, `listing_status`, `price_type`).
   Порядок членов enum в C# **менять нельзя** без миграции `ALTER TYPE` — метки завязаны на имена членов (нижний регистр).
5. **Listing удаляется мягко** (`DeletedAt` + глобальный query filter). Физически строки не удаляем — только файлы в хранилище.
6. **`ViewsCount`** меняется только через `Listing.RegisterView()` / отдельный атомарный UPDATE, не присваиванием извне.
7. **Id в сиде `subcategories` стабильны** — на них ссылается FK `listings.SubcategoryId`. Правка — отдельной миграцией.
8. **Секреты — только через env/`.env`** (в `.gitignore`). В `appsettings.json` секретов нет.
   Ключ подписи JWT (`Jwt__Key`) и ключ хеширования IP (`Security__IpHashKey`) — только из окружения; без `Jwt__Key` приложение не стартует.
9. **Все даты — UTC**, `DateTimeOffset` в коде, `timestamptz` в БД.
10. **Роль — только из claim токена.** Никогда не читать роль из тела/заголовка/query. При регистрации роль всегда `User`.
11. **Публикация объявлений требует подтверждённого контакта.** Какой именно — задаётся конфигом `Publishing:RequiredVerification` (`None|Email|Phone|Both`); на проде — `Email`. Проверка на сервере через `IPublishingPolicy`, не на фронте.

Целостность данных подстрахована **CHECK-констрейнтами на уровне БД** (длины полей, цена ≥ 0,
согласованность `Price`/`PriceType`) — они сработают, даже если валидация в коде пропустит ошибку.

---

## Журнал фич

### 1. Доменная модель, первая миграция и Docker-инфраструктура

**Что сделано:**
- Сущности: `User`, `Profile` (1:1), `Listing`, `Subcategory`, `ListingImage`, `Favorite`, `Conversation`, `Message`.
- 5 **native enum-типов** PostgreSQL: `Category`, `City`, `Condition`, `ListingStatus`, `PriceType`.
- Миграция `InitialSchema`: 8 таблиц, 5 enum-типов, 8 CHECK-констрейнтов, сид справочника подкатегорий (42 строки), индексы (в т.ч. частичные по «живым» объявлениям и GIN под будущий `SearchVector`).
- Docker-инфраструктура (`postgres`, `minio`, `adminer`, `api`) и `.env` с dev-значениями.
- CRUD объявлений (каркас): создавать может только авторизованный пользователь с подтверждённым по SMS телефоном; редактировать/удалять — только владелец; удаление мягкое.

**Почему именно так:**
- **Native enum вместо строк/чисел** — некорректное значение становится невозможным на уровне БД (проверено: город `kyiv` отклоняется самим типом). Хранение строкой этого не даёт.
- **Отдельная таблица `Subcategory` вместо свободного текста** — ссылочная целостность и единый справочник; пара (Category, Subcategory) валидируется и в БД (FK), и на сервере.
- **CHECK-констрейнты в БД, а не только атрибуты DTO** — данные защищены независимо от слоя приложения. Минимум длины (`Title ≥ 5`) через varchar не выразить — только CHECK.
- **`numeric(12,0)`** — цена в целых рублях ПМР; у рубля ПМР нет ISO-кода, поле валюты не заводим.
- **Мягкое удаление** — история (заказы, отзывы, диалоги) не должна рушиться при снятии объявления; восстановление возможно.
- **`Profile` отделён от `User`** — публичная/презентационная часть (имя, город, контакты) изолирована от учётных/секретных полей.
- **Подтверждение телефона по SMS (`User.PhoneVerified`)** — анти-фрод: барьер на массовую подачу фейковых объявлений.
- **Индексы** заточены под каталог: `(Status, CreatedAt desc)`, `(Category, City, Status)`, `(OwnerId, Status)` + GIN под полнотекст.

**Ключевые файлы:** `Domain/Entities/*`, `Domain/Enums/Enums.cs`,
`Infrastructure/Persistence/Configurations/*`, `Infrastructure/Persistence/AppDbContext.cs`,
`Infrastructure/Persistence/Migrations/*_InitialSchema.cs`.

---

### 2. Аутентификация (JWT + BCrypt) и подтверждение телефона по SMS

**Что сделано:**
- Собственная аутентификация (ASP.NET Identity не используется). Сущности `RefreshToken`, `PhoneVerificationCode` (миграция `AddAuth`).
- Пароли: BCrypt `EnhancedHashPassword`/`EnhancedVerify`, workFactor из конфига, пересчёт хеша при `NeedsRehash`, длина **8..72 байта UTF-8**, блок-лист частых паролей.
- JWT (HS256, 15 мин): claims `sub`/`role`/`sstamp`/`jti` (email в токен не кладём); валидация с `ClockSkew=0`, явным `HS256`, и событием `OnTokenValidated`, сверяющим `SecurityStamp` и бан через `IMemoryCache` (TTL 30с).
- Refresh (30 дней): 32 байта → base64url наружу, в БД только SHA-256; ротация при каждом обновлении; повторное использование отозванного токена → отзыв всей цепочки.
- Эндпоинты `/api/auth/{register,login,refresh,logout,logout-all,change-password}`.
- Подтверждение телефона **из профиля**, не при регистрации: `POST /api/me/phone/send-code` (сервер генерит код и «шлёт» SMS) + `/verify`. Без подтверждения нельзя публиковать объявления.
- Тесты (Testcontainers): идентичный ответ на неверный email/пароль, инвалидация токена после бана, отзыв цепочки при повторном refresh, отклонение 73-байтного пароля, отсутствие `PasswordHash`/`SecurityStamp`/`Role` в теле ответа.

**Почему именно так:**
- **Анти-энумерация:** один и тот же 401 и текст на неверный email и пароль; при отсутствии пользователя всё равно выполняется `Verify` против фиктивного хеша (нет timing-разницы); регистрация с занятым email даёт тот же нейтральный ответ, что и успешная (поэтому register **не** авторизует и не возвращает токены/юзера).
- **`sstamp` + короткий кэш:** без сверки SecurityStamp logout-all/смена пароля/бан не действовали бы до истечения access-токена (15 мин). Кэш 30с (в тестах 0 — мгновенно).
- **Refresh только хешем в БД + ротация + детект повтора** — украденный токен нельзя переиспользовать незаметно; повтор палит кражу и рвёт всю цепочку.
- **Длина пароля в байтах, а не символах** — BCrypt режет вход на 72 байтах; кириллический пароль в символах прошёл бы, а по байтам обрезался бы молча.
- **Телефон подтверждается из профиля** (а не на регистрации) — ниже трение на входе; код генерит сервер, хранится только его SHA-256, есть кулдаун и лимит попыток. Локальный ввод `0 775-…` нормализуется в `+373…` (в БД всегда E.164).
- **Rate-limit:** login 5/15м на (IP,email), register 3/ч на IP — против перебора и массовой регистрации.

**Ключевые файлы:** `Api/Auth/*` (JWT, refresh, SMS, rate-limit, SecurityStampValidator, PhoneNumber),
`Infrastructure/Auth/*` (BCrypt-хешер, политика паролей, блок-лист),
`Api/Controllers/AuthController.cs`, `Api/Controllers/PhoneVerificationController.cs`,
`Infrastructure/Persistence/Migrations/*_AddAuth.cs`, тесты `tests/GenesisMarket.Tests/Auth*`.

**Локальный запуск без Docker** дополнительно требует env-переменную `Jwt__Key` (≥32 байт),
иначе приложение не стартует (в Docker она приходит из `.env` через `docker-compose`).

---

### 3. Подтверждение почты и обобщённый механизм верификации

**Что сделано:**
- Механизм кодов **обобщён на оба канала**: одна сущность `VerificationCode` (`Channel` = Email/Phone, `Target`) вместо отдельной телефонной таблицы; общий `VerificationService` (генерация, хеш, кулдаун, лимит попыток, выставление флага) и диспетчер `IVerificationSender`.
- Добавлен флаг `User.EmailVerified`; эндпоинты `POST /api/me/email/{send-code,verify}` (симметрично телефону `/api/me/phone/...`), формат — 6-значный код.
- Отправка почты — `IEmailSender`: `SmtpEmailSender` (System.Net.Mail при заданном `Smtp:Host`) с dev-фолбэком `LogEmailSender` (пишет код в лог, локально работает без SMTP).
- **Гейт публикации вынесен в конфиг** `Publishing:RequiredVerification` (`None|Email|Phone|Both`), проверка через `IPublishingPolicy`. На проде — `Email`.
- Миграция `EmailVerificationAndGeneralize` (drop `phone_verification_codes`, add `users.EmailVerified`, create `verification_codes`).
- Тесты: почтовый флоу end-to-end (send→verify→публикация 201) и гейт (403 без подтверждения).

**Почему именно так:**
- **Обобщение вместо дублирования** — почта и телефон делят один код-механизм; добавить/убрать канал = конфиг, а не копипаст.
- **Телефон отложен, но остаётся** — на старте прод требует только почту (её проще доставить со своего сервера через SMTP, без платного SMS-оператора). Включить телефон обратно — сменой `Publishing:RequiredVerification`, без правок кода.
- **Dev-фолбэк почты в лог** — локальная разработка и тесты не требуют живого SMTP; код виден в логах (`[DEV EMAIL] …`).

**Ключевые файлы:** `Api/Auth/VerificationService.cs`, `Api/Auth/VerificationSender.cs`,
`Api/Auth/EmailSender.cs`, `Api/Auth/PublishingPolicy.cs`,
`Api/Controllers/{VerificationControllerBase,EmailVerificationController,PhoneVerificationController}.cs`,
`Domain/Entities/VerificationCode.cs`, `Infrastructure/Persistence/Migrations/*_EmailVerificationAndGeneralize.cs`.

---

### 4. Фирменное письмо с кодом (HTML-шаблон + логотип) и имя отправителя

**Что сделано:**
- Письмо с кодом (флоу `POST /api/me/email/send-code`) теперь шлётся по **фирменному HTML-шаблону** `Api/Auth/EmailTemplates/verification-code.html` (тёмная шапка, блок кода, блок-предупреждение, футер). Плейсхолдеры `{{CODE}}` и `{{LOGO_URL}}`.
- **Логотип** `gm-logo.png` вшивается в письмо как inline-вложение (`cid:`) — рендерится сразу, без хостинга картинки.
- Письмо стало **multipart/alternative**: HTML + текстовая версия (для клиентов без HTML).
- `VerificationEmailRenderer` читает шаблон и логотип из встроенных ресурсов **один раз при старте** и кеширует; подстановка — `string.Replace`.
- **Имя отправителя** (`Smtp:FromName`, по умолчанию «Genesis Market») — получатель видит `Genesis Market ‹адрес›` вместо голого e-mail. Реальная отправка — Gmail SMTP (App Password из env); при пустом `Smtp:Host` — dev-фолбэк в лог.
- Плейсхолдер брендового домена отправителя выровнен на будущий `genesis-hq.com`.

**Почему именно так:**
- **Шаблон и логотип — встроенные ресурсы + кеш** — нет чтения с диска на каждое письмо и нет зависимости от путей; деплой одним образом.
- **Логотип через `cid`, а не URL** — домена/хостинга картинок пока нет; inline-вложение показывается в Gmail сразу и не зависит от внешних ссылок.
- **HTML + текст вместе** — почтовый клиент выбирает лучший вариант; текстовая версия повышает доставляемость и работает везде.
- **Секреты не в коде** — Gmail App Password и адрес только в `.env`/env; в `appsettings.json` их нет.

**Ключевые файлы:** `Api/Auth/EmailTemplates/verification-code.html`, `Api/Auth/EmailTemplates/gm-logo.png`,
`Api/Auth/VerificationEmailRenderer.cs`, `Api/Auth/EmailSender.cs` (multipart + inline `cid`),
`Api/Auth/VerificationSender.cs`. Конфиг: `Smtp:FromName` + `SMTP_*` в `.env`/`docker-compose`.

**Замена канала/провайдера позже** — `IEmailSender` неизменен: вторая реализация (напр. MailKit
или транзакционный провайдер) и/или свой домен подключаются сменой значений в `.env`, без правок вызывающего кода.

---

### 5. Авторизация (политики, владение ресурсом, безопасные коды ответов)

**Что сделано:**
- **`FallbackPolicy = RequireAuthenticatedUser`** — всё защищено по умолчанию; забытый `[Authorize]` не откроет эндпоинт. Публичное помечено `[AllowAnonymous]` явно: каталог (`GET /api/listings`, `/{id}`), `register/login/refresh`, health.
- **Политики** в DI: `Moderator` (role ∈ {Moderator, Admin}), `Admin`, `NotBanned`, `ResourceOwner`.
- **`ICurrentUser`** (scoped) — единственная точка чтения текущего пользователя из claims; контроллеры и хендлеры берут его отсюда.
- **Владение ресурсом — через `IAuthorizationHandler`**: `ResourceOwnerRequirement` + обобщённый `ResourceOwnerHandler` для `IOwnedResource` (сейчас `Listing`; `Review`/`SavedSearch` подключатся, реализовав интерфейс). В контроллере нет `listing.OwnerId == currentUserId` — только `AuthorizationService.AuthorizeAsync(...)`.
- **Коды ответов:** «нет объекта» и «чужой объект» → одинаковый **404** для приватных ресурсов; **403** только там, где существование публично (каталог) — например удаление чужого объявления.
- **`docs/authorization-matrix.md`** — таблица «ресурс × роль × операция» со ссылками file:line, где правило обеспечено.
- Тесты: аноним → 401, чужой → 403, владелец → 204, модератор → 204; гость смотрит каталог и регистрируется.

**Почему именно так:**
- **Fallback-политика** — «безопасно по умолчанию»: новый эндпоинт закрыт, пока явно не открыт. Публичные точки перечислены и видны.
- **Обобщённый владельческий хендлер** — одна проверка на все владельческие сущности; меньше дублирования и меньше шансов забыть проверку в контроллере.
- **404 вместо 403 для приватных ресурсов** — 403 подтверждает существование объекта, что уже утечка; 403 оставлен только для публично существующих (каталог).
- **`ICurrentUser` как единственный ридер claims** — нет разрозненного чтения `sub`/`role` по коду.

**Ключевые файлы:** `Api/Auth/CurrentUser.cs`, `Api/Auth/ResourceOwnerAuthorization.cs`,
`Api/Auth/AuthServiceCollectionExtensions.cs` (политики + FallbackPolicy),
`Domain/Common/IOwnedResource.cs`, `Api/Controllers/ListingsController.cs` (AllowAnonymous + AuthorizeAsync),
`docs/authorization-matrix.md`, тесты `tests/GenesisMarket.Tests/AuthorizationTests.cs`.

---

<!-- №6 (профили пользователей) — в параллельной ветке feature/profiles. -->

### 7. Жизненный цикл объявления

**Что сделано:**
- Эндпоинты: `POST /api/listings` (черновик или сразу на публикацию), `GET /{id}`, `GET /by-slug/{slug}`,
  `PATCH /{id}` (только владелец), `DELETE /{id}` (снятие с публикации → `Archived`),
  `POST /{id}/publish`, `GET /api/me/listings` (свои, с фильтром по статусу).
- **Валидация FluentValidation** (`CreateListingRequestValidator`/`UpdateListingRequestValidator`):
  Title 10..100, Description 20..5000, цена 0..`Listings:MaxPrice`, согласованность `Price`/`PriceType`,
  enum-поля. Пара (Category, Subcategory) и лимиты — в контроллере (нужен доступ к БД).
- **Slug** — транслитерация кириллицы + короткий хеш Id (`SlugGenerator`), уникальный индекс БД,
  при коллизии (`23505`) — повторная генерация со случайным суффиксом. Slug не меняется при PATCH.
- **Премодерация** — `IListingModerationPolicy`: <3 опубликованных ИЛИ аккаунт моложе 7 дней ⇒ `PendingReview`,
  иначе `Active`. Пороги в конфиге `Listings`.
- **Лимит** `Listings:MaxActivePerUser` (30) на статусы «в обороте» (Active+PendingReview) — проверяется при публикации.
- **Дубликаты** при создании: тот же нормализованный Title в той же категории среди «в обороте» → 409.
- **Счётчик просмотров** — `IListingViewCounter`: атомарный `SET "ViewsCount" = "ViewsCount" + 1`,
  не чаще одного раза в час на пару (ListingId, IpHash) через `IMemoryCache`.
- **Защита от mass assignment в PATCH**: отдельный `UpdateListingRequest`; Status/OwnerId/ViewsCount/Slug/
  CreatedAt/PublishedAt через него не меняются. `PATCH` чужого объявления → **404** (owner-only фильтром).
- Миграция `AddListingSlug`. Тесты: чужой не редактирует (404); PATCH `status:Active` не публикует; 31-е активное → 409.

**Почему именно так:**
- **Гейт «подтверждённый контакт» перенесён с создания на публикацию** — черновик (невидим в каталоге)
  можно создать без верификации; публикация (делает объявление публичным) требует подтверждённой почты.
- **Slug с хешем Id** — коллизии практически невозможны, но уникальность всё равно держит БД; сервер не доверяет уникальности «на глаз».
- **Инкремент просмотров отдельным UPDATE** — без read-modify-write (нет гонки), троттлинг по (Listing, IpHash) режет накрутку.
- **Owner-only PATCH через фильтр `OwnerId == me` → 404** — не раскрывает даже факт существования чужого объявления при попытке правки; снятие с публикации (DELETE) доступно и модератору (403 для прочих).
- **Лимит/дубликаты по «в обороте» (Active+PendingReview)** — премодерация не позволяет обойти лимит, накопив PendingReview.

**Ключевые файлы:** `Api/Controllers/ListingsController.cs`, `Api/Listings/*`
(`ListingOptions`, `SlugGenerator`, `IListingModerationPolicy`, `IListingViewCounter`, валидаторы),
`Api/Contracts/ListingDtos.cs`, `Infrastructure/Persistence/Migrations/*_AddListingSlug.cs`,
тесты `tests/GenesisMarket.Tests/ListingLifecycleTests.cs`.
### 6. Профили пользователей (приватный/публичный, soft-delete)

**Что сделано:**
- **Приватный профиль владельца:** `GET /api/me` (всё, кроме PasswordHash и SecurityStamp),
  `PATCH /api/me`, `POST /api/me/avatar`, `DELETE /api/me`.
- **Публичный профиль:** `GET /api/users/{id}/public` (анонимно) — DisplayName, City, AvatarUrl,
  дата регистрации **только месяц+год**, ActiveListingsCount, AverageRating/ReviewsCount, PhoneVerified.
  Ни email, ни телефона, ни точной даты, ни Role, ни IsBanned.
- **Защита от mass assignment:** PATCH принимает отдельный DTO `UpdateMeRequest` с фиксированным
  набором полей; Role/IsBanned/PasswordHash/SecurityStamp/Email в него не входят и не редактируются.
- **Смена телефона** сбрасывает `PhoneVerified`. Нормализация в E.164 (+373/+7; прочие коды —
  по флагу `Phone:AllowOtherCountries`).
- **`DELETE /api/me` — soft delete + анонимизация в одной транзакции:** `IsDeleted=true`,
  email → `deleted-{id}@invalid`, DisplayName → «Удалённый пользователь», обнуление телефона/телеграма/аватара,
  смена SecurityStamp, отзыв refresh-токенов, архивация активных объявлений. Токены удалённого
  перестают работать сразу (`SecurityStampValidator` отклоняет удалённых).
- Миграция `AddUserIsDeleted` (аддитивная колонка `users.IsDeleted`).
- Тесты: PATCH с `role:Admin` не меняет роль; публичный профиль без email/телефона; `GET /api/me`
  без PasswordHash/SecurityStamp; удаление анонимизирует, архивирует объявления и инвалидирует токен.

**Почему именно так:**
- **Отдельный DTO вместо биндинга в User** — единственная надёжная защита от mass assignment
  (нельзя прислать `role`/`isBanned` и повысить себя).
- **Публичный профиль — отдельный DTO с минимумом** — по умолчанию не «забыть» скрыть email/телефон.
  Точная дата регистрации огрубляется до месяца (меньше деанонимизации).
- **Мягкое удаление + анонимизация в транзакции** — историю (объявления, будущие отзывы/диалоги)
  нельзя рвать, но персональные данные нужно убрать; всё атомарно.
- **Смена телефона сбрасывает подтверждение** — иначе можно было бы «унаследовать» верификацию на чужой номер.

**Ключевые файлы:** `Api/Controllers/MeController.cs`, `Api/Controllers/UsersController.cs`,
`Api/Contracts/ProfileDtos.cs`, `Api/Auth/PhoneNumber.cs` (+`PhoneOptions`),
`Api/Auth/SecurityStampValidator.cs` (проверка IsDeleted),
`Infrastructure/Persistence/Migrations/*_AddUserIsDeleted.cs`, тесты `tests/GenesisMarket.Tests/ProfileTests.cs`.

### 8. Каталог объявлений (курсорная пагинация, count с кэшем)

**Что сделано:**
- `GET /api/listings` — витрина со свободными фильтрами: `category`, `subcategory`, `cities` (несколько),
  `priceFrom`/`priceTo` (long), `condition`, `priceType`, `sort`, `cursor`, `limit`.
- **Курсорная (keyset), не offset:** курсор — base64url от кортежа (значение поля сортировки, `Id`-тайбрейкер).
  Ответ `{ items, nextCursor, hasMore }`. `limit` по умолчанию 20, максимум 50 (**больше — обрезается, не ошибка**).
- **Сортировка строго из белого списка** (`new | price_asc | price_desc | popular`) через enum `CatalogSort`;
  ORDER BY не собирается из строки запроса. Договорные цены (`Price IS NULL`) при сортировке по цене — всегда в конце.
- **Только `Active` попадает в каталог** — `Sold/Archived/PendingReview/Rejected` и черновики не видны никогда,
  включая случай, когда запрос делает их владелец (эндпоинт вообще не ветвится по владельцу).
- **Валидация:** `cities` > 7 → 400; `priceFrom > priceTo` → 400; неизвестный `sort` → 400; битый/чужой `cursor` → 400
  (в курсоре хранится маркер сортировки и сверяется с запросом).
- `GET /api/listings/count` — общее количество по тем же фильтрам, **кэш `IMemoryCache` на 60 c** по набору
  фильтров (точный `COUNT` на каждый запрос не делаем).
- **DTO карточки `ListingCardResponse`**: Id, Slug, Title, Price, PriceType, City, Category, FirstImageUrl,
  PublishedAt, IsBumped. **Телефона, email и UserId владельца в карточке нет.**
- **Производительность:** `AsNoTracking` на всех чтениях; первое изображение — коррелированным подзапросом
  (`Images.OrderBy(SortOrder).Select(ThumbKey).FirstOrDefault()`), **без `Include` всей коллекции**;
  тянем `limit + 1` строку — так `hasMore` без отдельного запроса.
- Схему **не меняли** (миграции нет): «new» сортируется по `CreatedAt`, чтобы задействовать индекс шага 1
  `IX_listings_Status_CreatedAt`.

**Планы запросов** (EXPLAIN ANALYZE, 20k строк, см. диагностику ниже) — используются индексы шага 1:
- `Active + sort=new` → `Index Scan using IX_listings_Status_CreatedAt` + Incremental Sort (CreatedAt presorted);
- `Active + category + city + sort=new` → `Bitmap Index Scan on IX_listings_Category_City_Status` + top-N heapsort;
- `Active + category + priceRange + sort=price_asc` → тот же индекс для фильтра + quicksort по цене
  (индекса под сортировку по цене в шаге 1 нет — это ожидаемо, объём отсортированных строк ограничен фильтром).

**Почему именно так:**
- **Keyset, а не offset** — стабильная пагинация без «съезда» при вставках и без роста стоимости на глубоких страницах;
  `Id`-тайбрейкер даёт строго детерминированный порядок при равных значениях поля сортировки.
- **`sort` через enum-белый-список** — исключает SQL-инъекцию через ORDER BY и «случайные» поля сортировки.
- **`count` кэшируется** — точный `COUNT(*)` по растущей таблице дорог; для витрины достаточно приблизительного числа,
  обновляемого раз в минуту.
- **Карточка — отдельный DTO с минимумом** — по умолчанию нельзя «забыть» скрыть контакты/владельца.
- **«new» по `CreatedAt`** — переиспользуем существующий индекс шага 1 вместо новой миграции в прод.

**Ключевые файлы:** `Api/Controllers/ListingsController.cs` (`GetAll`, `Count`),
`Api/Listings/CatalogQueryBuilder.cs` (фильтры/сортировки/keyset), `Api/Listings/CatalogCursor.cs` (кодек курсора),
`Api/Contracts/CatalogDtos.cs`, тесты `tests/GenesisMarket.Tests/CatalogTests.cs`,
диагностика планов `tests/GenesisMarket.Tests/CatalogExplainDiagnostics.cs` (помечена `Skip`, запускать вручную).

> Побочный фикс: в `appsettings.json` секция `Listings` не закрывалась `}` перед `Phone` — файл был невалидным
> JSON и приложение не стартовало. Исправлено.

### 9. Поиск по объявлениям (PostgreSQL FTS + fuzzy-fallback)

**Что сделано:**
- **`SearchVector`** — `tsvector`, **`GENERATED ALWAYS AS (… ) STORED`**: `setweight(to_tsvector('russian', Title),'A')`
  ‖ `setweight(to_tsvector('russian', Description),'B')`. Заголовок весит больше описания. GIN-индекс.
  Колонку СУБД считает сама — приложение её только читает.
- **`GET /api/listings?q=…`** — комбинируется со всеми фильтрами шага 8 (category/city/price/…).
  Матч через **`websearch_to_tsquery('russian', @q)`** — безопасно разбирает кавычки/OR/операторы, не падает
  на произвольном вводе (в отличие от `to_tsquery`). Ввод **только параметром**, без интерполяции строк.
- **Сортировка `sort=relevance`** — `ts_rank_cd`; при поиске это дефолт. Курсор режима — `(rank, id)`
  (rank как round-trip float в base64-курсоре). С q работают и обычные сортировки (new/price/popular).
- **Длина `q` ограничена 100 символами** на сервере (обрезается), нормализация trim + lower.
- **Подсветка** — `ts_headline` со `StartSel=<mark>/StopSel=</mark>`. **XSS-безопасно:** исходный `Title`
  экранируется (`& < >`) прямо в SQL **до** подсветки, поэтому единственный HTML в ответе — вставленные СУБД
  `<mark>`. Отдаётся в `ListingCardResponse.TitleHighlight` (null без q).
- **Опечатки (fuzzy-fallback)** — расширение **`pg_trgm`**: если FTS дал 0 на первой странице, отдельным
  запросом ищем по `similarity(lower(Title), @q) > 0.3`. Режимы **не смешиваются** в одном запросе.
- **`SearchMisses`** — запросы с окончательно нулевой выдачей (ни FTS, ни fuzzy) пишутся в отдельную таблицу:
  данные о том, чего ищут, но в каталоге нет.
- Миграция `AddFullTextSearch` — **сырым SQL**: колонку-генерацию нельзя получить через `ALTER COLUMN`,
  поэтому DROP+ADD с пересозданием GIN; плюс `CREATE EXTENSION pg_trgm` и таблица `search_misses`.

**Почему именно так:**
- **STORED generated-колонка вместо триггера/ручного обновления** — вектор всегда согласован с Title/Description,
  негде забыть пересчитать; приложение не пишет в неё.
- **`websearch_to_tsquery`, а не `to_tsquery`** — первый не бросает ошибку на «мусорном» вводе пользователя
  (кавычки, `&|!:*`), поэтому спецсимволы не роняют запрос и не меняют смысл (есть тест).
- **Экранирование до `ts_headline`** — иначе `<script>` в заголовке объявления утёк бы в ответ как рабочий HTML
  (XSS через заголовок). Экранируем в SQL, метки `<mark>` добавляются уже поверх безопасного текста.
- **Fuzzy — отдельный fallback, а не UNION с FTS** — не «зашумляет» точную выдачу; включается только когда
  точный поиск пуст.
- **Единый источник SQL-выражения вектора** (`ListingConfiguration.SearchVectorSql`) — модель и миграция
  ссылаются на одну константу, не разъезжаются.

**Ключевые файлы:** `Api/Listings/CatalogQueryBuilder.cs` (FTS-фильтр, relevance-order/keyset, trigram-fallback),
`Api/Listings/SearchHighlighter.cs` (ts_headline + экранирование), `Api/Controllers/ListingsController.cs`
(`SearchAsync`/`TrigramFallbackAsync`), `Domain/Entities/SearchMiss.cs`,
`Infrastructure/Persistence/Configurations/ListingConfiguration.cs` (SearchVector), `AppDbContext.cs` (pg_trgm),
`Infrastructure/Persistence/Migrations/*_AddFullTextSearch.cs`, тесты `tests/GenesisMarket.Tests/CatalogSearchTests.cs`.

---

### 10. Загрузка изображений объявлений (обработка + приватный MinIO + outbox)

**Что сделано:**
- Эндпоинты под объявлением: `POST /api/listings/{id}/images` (multipart, один файл), `DELETE /api/listings/{id}/images/{imageId}`, `PATCH /api/listings/{id}/images/order` (полный набор Id в новом порядке) и публичный `GET /api/listings/{id}/images` (список с presigned-ссылками).
- Серверные проверки **до любой обработки**: владелец (`OwnerId` в фильтре ⇒ чужое/несуществующее = 404), не больше **8** изображений на объявление, **10 МБ** на файл (лимит и в `[RequestSizeLimit]`, и в `[RequestFormLimits(MultipartBodyLengthLimit)]`).
- Тип файла — **по содержимому** (`Image.DetectFormatAsync`, magic bytes), не по расширению и не по `Content-Type`. Разрешены JPEG/PNG/WebP; иначе `415`.
- Защита от decompression bomb: объявленные размеры проверяются по заголовку (`Image.IdentifyAsync`) **до декодирования** — при `width*height > 50 млн` пикселей `400`; плюс лимит единичной аллокации у `Configuration.Default.MemoryAllocator`.
- Обработка (ImageSharp): применяется EXIF-ориентация → **полностью снимаются метаданные** (EXIF/IPTC/XMP — там GPS продавца) → ресайз (оригинал ≤ 1600px по длинной стороне, превью 400×300 crop «cover») → перекодирование в **WebP q82**.
- Хранение (MinIO): бакет приватный, серверные ключи `listings/{listingId}/{guid}.webp` (+ `_thumb`); имя файла из запроса **нигде не используется**. Отдача — только **presigned URL, TTL 1 час**. Бакет создаётся при старте (`MinioBucketInitializer`), публичная политика не выставляется.
- Удаление объектов из хранилища — через **transactional outbox** (`OutboxMessage`, миграция `AddOutboxMessages`): DELETE изображения кладёт сообщения в одной транзакции с удалением строки, фоновый `ObjectDeletionOutboxProcessor` (BackgroundService) удаляет объекты из MinIO вне HTTP-запроса.
- Тесты (Testcontainers, MinIO подменён in-memory фейком, процессор — настоящий): `.jpg` с PHP-содержимым отклоняется; JPEG с GPS после обработки не содержит EXIF и стал WebP; загрузка в чужое объявление = 404; смена порядка; DELETE удаляет строку и ставит два сообщения в outbox.

**Почему именно так:**
- **Тип по содержимому, а не по расширению/Content-Type** — расширение и заголовок задаёт клиент; полагаться на них = принять `.jpg` с исполняемым содержимым. Определяем по magic bytes.
- **Снятие EXIF обязательно** — координаты съёмки в метаданных фото квартиры = прямая утечка адреса продавца. Ориентацию применяем **до** удаления метаданных, иначе портретные фото развернутся.
- **Проверка размеров до декодирования** — декодировать «бомбу» 60000×60000, чтобы потом отклонить, значит уже съесть память; читаем только заголовок.
- **Приватный бакет + presigned URL** — прямые постоянные ссылки на объект наружу не публикуются; доступ ограничен по времени и генерируется бэкендом.
- **Outbox для удаления объектов** — HTTP-запрос не должен ждать MinIO и падать, если хранилище недоступно; удаление гарантированно доедет фоновым обработчиком (повтор при сбое), не блокируя пользователя.

**Ключевые файлы:** `Api/Controllers/ListingImagesController.cs`, `Api/Contracts/ListingImageDtos.cs`,
`Infrastructure/Imaging/{IImageProcessor,ImageSharpImageProcessor,ImageExceptions}.cs`,
`Infrastructure/Storage/{IObjectStorage,MinioObjectStorage,MinioBucketInitializer,ObjectDeletionOutboxProcessor}.cs`,
`Domain/Entities/OutboxMessage.cs`, `Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs`,
`Infrastructure/Persistence/Migrations/*_AddOutboxMessages.cs`, тесты `tests/GenesisMarket.Tests/ListingImagesTests.cs`.

---

### 11. Раскрытие контактов продавца (анти-скрейпинг)

**Что сделано:**
- Единственный эндпоинт, отдающий телефон: `GET /api/listings/{id}/contact` → `{ phone, telegramUrl, viberUrl, whatsappUrl }`. Отдаётся **только** если объявление `Active`, продавец не забанен и `Profile.ShowPhoneInListing = true` (плюс телефон вообще задан); иначе — **единый 404 без объяснения причины**.
- Deeplink'и строятся на сервере из E.164-телефона и флагов профиля: `https://t.me/{username}`, `viber://chat?number=%2B{digits}`, `https://wa.me/{digits}`; выключенный канал ⇒ соответствующее поле `null`.
- **Телефон нигде больше в API не появляется** — ни в `ListingResponse`, ни в карточке каталога, ни в публичном профиле, ни в поиске (в проекциях его просто нет).
- Анти-скрейпинг: rate-limit с партиционированием по (IpHash, UserId) — **10/час** анонимам (по IpHash), **30/час** авторизованному (по UserId); анонимам добавлена задержка ответа **300–600 мс**; каждое раскрытие пишется в `ContactReveals` (`ListingId`, `ViewerUserId?`, `IpHash`, `CreatedAt`), где IP хранится **только как HMAC-SHA256** (ключ `Security:IpHashKey` из env); при **> 50** раскрытиях с одного IpHash за час — `warning` в лог.
- `contactRevealCount` в `ListingResponse` — агрегат из `ContactReveals`, считается отдельным запросом (в списке «мои объявления» — одним `GROUP BY`, без N+1).
- Тесты (Testcontainers): телефон отсутствует в `GET /api/listings` и `GET /api/listings/{id}`; успешное раскрытие отдаёт телефон и серверные deeplink'и и пишет строку в журнал; выключенные каналы дают `null`; `ShowPhoneInListing = false` ⇒ 404 и записи нет; превышение анонимного лимита ⇒ 429 с `Retry-After`.

**Почему именно так:**
- **Единый 404 для всех причин отказа** (нет объявления / не Active / бан / показ выключен / нет телефона) — не даёт скрейперу отличить «скрыто» от «нет»; телефон и username читаются из БД **только** внутри этого экшена.
- **IP только как HMAC** — журнал раскрытий не хранит сырой IP (тот же принцип, что у счётчика просмотров); без ключа `Security:IpHashKey` IP не сохраняется.
- **Задержка анонимам** делает массовый обход дороже единичного просмотра, не мешая обычному пользователю; авторизованных не тормозим (у них выше лимит и они опознаваемы).
- **`contactRevealCount` отдельным запросом** — агрегат по append-only журналу не тащим джойном в каждую выборку объявлений; в списковых сценариях считаем батчем `GROUP BY`, чтобы не ловить N+1.

**Ключевые файлы:** `Api/Controllers/ListingsController.cs` (`GetContact`), `Api/Contracts/ContactDtos.cs`,
`Api/Listings/{ContactRevealService,ContactLinkBuilder,ContactRevealOptions}.cs`, `Api/Auth/IpHasher.cs` (переиспользуется),
`Domain/Entities/ContactReveal.cs`, `Infrastructure/Persistence/Configurations/ContactRevealConfiguration.cs`,
`Infrastructure/Persistence/Migrations/*_AddContactReveals.cs`, тесты `tests/GenesisMarket.Tests/ContactRevealTests.cs`.

---

## Известные ограничения

- **Сид `subcategories` — провизорный.** Источник правды `pmr_market_prompt.md` (раздел CATEGORIES)
  в репозитории отсутствует; текущие 42 подкатегории — заглушка, заменить на реальный список.
- **`FirstImageUrl` в карточке каталога — это `ThumbKey` (ключ объекта), а не presigned URL.** Загрузка и
  обработка фото уже есть (фича 10), но проекция каталога пока отдаёт ключ; подписывать ссылки в списковой
  выдаче (батчем) — отдельный шаг, чтобы не генерировать presigned на каждую карточку синхронно.
- **`IsBumped` в карточке каталога всегда `false`.** Продвижение (bump) — шаг 7; отдельной колонки в схему
  умышленно не добавляли, чтобы не угадывать будущую модель промо. Когда появится — здесь будет реальное условие.
- **SMS — заглушка `DevSmsSender`** (код в лог `[DEV SMS] …`). Подтверждение телефона доступно,
  но на проде не гейтит публикацию (гейт = почта). Реальная отправка — вторая реализация `ISmsSender`
  (SMS-провайдер или Telegram/Viber-бот).
- **Почта через `System.Net.Mail.SmtpClient`** — базовый вариант на старте; при пустом `Smtp:Host`
  код пишется в лог (`[DEV EMAIL] …`). Позже разумно перейти на MailKit.
- **Блок-лист паролей — курируемое подмножество**, не полный top-1000. Расширить `Auth/common-passwords.txt`.
- **Rate-limit — in-memory** (на инстанс). При нескольких инстансах API нужен общий стор (напр. Redis).
- **Аватар (`POST /api/me/avatar`) — минимальная реализация**: валидация типа по magic bytes + размер,
  серверный ключ, сохранение в MinIO; `AvatarUrl` хранит ключ объекта. Обработка (ресайз, снятие EXIF,
  WebP) и presigned-URL для отдачи — шаг 8; заменит текущую реализацию.
- **AverageRating/ReviewsCount в публичном профиле — заглушки** (null/0): появятся с отзывами (шаг 11).
