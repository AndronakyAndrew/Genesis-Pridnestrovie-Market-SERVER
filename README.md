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

### 12. Избранное (идемпотентность на уровне БД + денормализованный счётчик)

**Что сделано:**
- Три эндпоинта: `POST /api/listings/{id}/favorite`, `DELETE /api/listings/{id}/favorite`, `GET /api/me/favorites` (курсорная пагинация, те же DTO `ListingCardResponse`/`CatalogPageResponse`, что и каталог). Все — `[Authorize]`.
- **Идемпотентность на уровне БД:** составной первичный ключ `favorites (UserId, ListingId)`. Повторный `POST` возвращает **200** (не 409) и **не создаёт дубль**: сначала проверка `AnyAsync`, а гонку двух параллельных вставок ловим по `23505` и тоже отдаём успех. `DELETE` идемпотентен — 204 даже если записи не было (`ExecuteDelete`, 0 строк).
- **Нельзя добавить в избранное собственное объявление** — `400`; чужое soft-deleted (скрыто глобальным фильтром) — `404`.
- **`isFavorite` в карточке каталога — одним запросом на всю страницу:** после сборки страницы собираем `Id` карточек и делаем один `WHERE UserId = @me AND ListingId IN (...)`, помечая совпавшие (`OkPageAsync`). Не по запросу на карточку. Для анонима и пустой страницы — без доп. запроса.
- **`favoritesCount` — денормализованное поле `listings.FavoritesCount`, а не `COUNT` на каждый запрос.** Поддерживается триггером БД `trg_favorites_count` (функция `favorites_count_sync`): `AFTER INSERT/DELETE ON favorites` инкрементит/декрементит счётчик в той же транзакции. Идемпотентный `POST` строку не вставляет ⇒ триггер не срабатывает ⇒ счётчик не задваивается. `CHECK (\"FavoritesCount\" >= 0)`. Существующие записи забэкфилены в миграции.
- **Лимит 500 записей на пользователя** — проверка `CountAsync` перед вставкой, при достижении `409`.
- **Архивированное/удалённое объявление остаётся в избранном с флагом `isUnavailable`, а не исчезает молча:** список тянется с `IgnoreQueryFilters()`, `isUnavailable = Status != Active || DeletedAt != null`.
- В детальной карточке (`ListingResponse`) добавлены `favoritesCount` и `isFavorite`.
- Тесты (Testcontainers): идемпотентный POST без дубля; запрет своего; 404 на несуществующее; декремент и идемпотентный DELETE; `isFavorite`+счётчик в каталоге (и `false` анониму); архив с `isUnavailable`; лимит 500 → 409; курсорная пагинация без дыр; требование авторизации.

**Почему именно так:**
- **Составной PK вместо суррогатного** — идемпотентность гарантирует БД, а не приложение; дубль физически невозможен.
- **Триггер, а не пересчёт `COUNT`** — карточка каталога отдаёт счётчик без агрегата на каждый запрос; счётчик всегда согласован с таблицей, т.к. меняется в одной транзакции со вставкой/удалением.
- **`isFavorite` батчем** — тот же приём, что у `contactRevealCount`/`firstImageUrl`: один `WHERE IN` на страницу вместо N+1.
- **`IgnoreQueryFilters` в избранном** — soft-delete не должен молча «терять» карточку из списка пользователя; вместо исчезновения — честный флаг недоступности.

**Ключевые файлы:** `Api/Controllers/FavoritesController.cs`, `Api/Contracts/FavoriteDtos.cs`,
`Api/Controllers/ListingsController.cs` (`OkPageAsync`, `IsFavoriteAsync`, `Map`), `Api/Contracts/{CatalogDtos,ListingDtos}.cs`,
`Api/Listings/CatalogQueryBuilder.cs` (`CatalogRow.FavoritesCount`), `Domain/Entities/Listing.cs` (`FavoritesCount`),
`Infrastructure/Persistence/Configurations/{FavoriteConfiguration,ListingConfiguration}.cs`,
`Infrastructure/Persistence/Migrations/*_AddFavorites.cs` (колонка + триггер + бэкфилл), тесты `tests/GenesisMarket.Tests/FavoritesTests.cs`.

---

### 13. Слой доверия: отзывы и жалобы

**Что сделано:**
- Две сущности (миграция `AddTrustLayer`): `Review` (Id, ListingId, AuthorId, TargetUserId, Rating 1..5, Text ≤1000, IsHidden, HiddenByUserId?) и `Report` (TargetType `{Listing,User,Review}`, TargetId, ReporterId?, ReporterIpHash?, Reason `{Spam,Fraud,Prohibited,WrongCategory,Duplicate,PriceViolation,Other}`, Comment ≤500, Status `{New,InReview,Resolved,Rejected}`, ResolvedByUserId?, ResolvedAt?, Resolution?). Enum-ы жалоб — строками (`HasConversion<string>()`), не native-типами.
- **Отзывы.** `POST /api/reviews` (`[Authorize]`), `GET /api/users/{id}/reviews` (публичный, курсорная пагинация, скрытые не отдаются), `PUT /api/reviews/{id}` (правка автором в окне 24ч), `POST /api/reviews/{id}/hide` (`[Authorize(Policy="Moderator")]`, идемпотентно).
- **Гейт анти-накрутки:** оставить отзыв можно, только если этот пользователь ранее вызывал `/contact` по объявлению (проверка `AnyAsync` по `ContactReveals` с `ViewerUserId`). Нет ни одной попытки контакта → `403`.
- **Один отзыв на пару (AuthorId, ListingId)** — уникальный индекс; повтор → `409` (гонку ловим по `23505`). **Отзыв самому себе** (автор = владелец объявления) → `400`, проверяется до гейта контактов.
- **`AverageRating`/`ReviewsCount` у пользователя — денормализованные поля,** пересчёт **триггером БД `trg_reviews_rating`** (функция `reviews_rating_sync`, `AFTER INSERT/UPDATE/DELETE ON reviews`) в той же транзакции, что и запись/правка/скрытие. Скрытые отзывы (`IsHidden`) в агрегат не входят; `AverageRating = NULL`, когда видимых отзывов нет. Публичный профиль (`PublicProfileResponse`) теперь отдаёт реальные значения.
- **Жалобы.** `POST /api/reports` — **доступен анонимам** (мошенничество замечает и незарегистрированный). Rate-limit in-memory: **5/час на IpHash** (аноним), **20/час на пользователя** (`IReportRateLimiter`, тот же приём, что у раскрытия контактов). Дубликат жалобы того же репортёра на тот же объект (среди открытых `New/InReview`) → `200` **без новой записи**; для анонима репортёр опознаётся по `ReporterIpHash` (HMAC IP, сырой IP не хранится). Несуществующий объект → `404`.
- **Автоматика модерации:** при **≥3 независимых** открытых жалобах `Fraud`/`Prohibited` на одно объявление — автоперевод `Active → PendingReview` и подъём `ModerationPriority` (в начало очереди). Независимость = различные `ReporterId`, для анонимов — различные `ReporterIpHash`. Порог и приоритет — в конфиге (секция `Trust`). Запись жалобы и автоперевод — в одной транзакции.
- Тесты (Testcontainers): отзыв без предшествующего contact-reveal → 403; второй отзыв на то же объявление → 409; отзыв самому себе → 400; пересчёт агрегата (среднее из двух); скрытие модератором убирает отзыв из выдачи и из агрегата; правка автором в окне и запрет чужому; дедуп жалобы; 3 независимых Fraud → PendingReview; 2 не хватает; 404 на несуществующий объект; анонимный приём + rate-limit.

**Почему именно так:**
- **Гейт по `ContactReveals`, а не по заказам** — сделки офлайн, «заказа» может не быть; но раскрытие контакта — минимальный след реального намерения, отсекающий накрутку без единой попытки связаться.
- **Триггер для агрегата рейтинга** — как у `favoritesCount`: профиль отдаёт рейтинг без `AVG`/`COUNT` на каждый запрос, и агрегат всегда согласован (пересчёт в одной транзакции с изменением отзыва, включая скрытие).
- **Жалобы анонимам + `ReporterIpHash`** — приём сигнала важнее, чем регистрация репортёра; HMAC IP даёт дедуп и подсчёт независимости, не сохраняя сырой IP (как в анти-скрейпинге контактов).
- **Порог 3 независимых, а не N жалоб** — один пользователь (или один IP) не может в одиночку отправить объявление на модерацию; нужна независимая корроборация.
- **`ModerationPriority` в `Listing`** — «начало очереди» выражено полем сортировки, а не отдельной таблицей очереди (её ещё нет); когда появится UI модерации — сортировка по `ModerationPriority DESC` уже готова.

**Ключевые файлы:** `Api/Controllers/{ReviewsController,ReportsController}.cs`, `Api/Contracts/{ReviewDtos,ReportDtos}.cs`,
`Api/Trust/{TrustOptions,ReportRateLimiter,TrustServiceCollectionExtensions}.cs`, `Api/Controllers/UsersController.cs` (реальные `AverageRating`/`ReviewsCount`),
`Domain/Entities/{Review,Report}.cs`, `Domain/Enums/Enums.cs` (три enum-а жалоб), `Domain/Entities/User.cs` (`AverageRating`/`ReviewsCount`), `Domain/Entities/Listing.cs` (`ModerationPriority`),
`Infrastructure/Persistence/Configurations/{ReviewConfiguration,ReportConfiguration}.cs`,
`Infrastructure/Persistence/Migrations/*_AddTrustLayer.cs` (таблицы + триггер рейтинга), тесты `tests/GenesisMarket.Tests/{ReviewsTests,ReportsTests}.cs`.

---

### 14. Инструменты модератора (очередь, действия, аудит-журнал)

Рабочая поверхность модератора. Все ручки под `[Authorize(Policy = "Moderator")]` (роль
`Moderator`/`Admin` **только из claim JWT** — никаких хардкод-списков email/админов в коде).
Обычный пользователь → `403`, аноним → `401` на любой `/api/moderation/*`.

- **`GET /api/moderation/queue`** — единый поток: объявления `PendingReview` **и** открытые жалобы
  (`Status=New`). Сортировка: **сначала автофлаги** (`ModerationPriority DESC`), затем по дате
  (старые раньше — FIFO). **Курсорная пагинация** (`ModerationCursor`: `priority|ticks|id`, keyset над
  двумя источниками — по `limit+1` из каждого, слияние в памяти). Фильтры: `type` (`listing`|`report`),
  `reason` (сужает до жалоб), `priority` (мин. приоритет — исключает жалобы).
- **`GET /api/moderation/listings/{id}`** — полная карточка, включая скрытые поля (владелец, приоритет,
  статус даже у снятого/удалённого — `IgnoreQueryFilters`) и открытые жалобы по объявлению.
- **`POST /api/moderation/listings/{id}/approve`** — `PendingReview → Active`, сброс приоритета.
- **`POST /api/moderation/listings/{id}/reject`** `{ reason, comment }` — `→ Rejected`; **уведомление автора
  с текстом причины через Outbox** (`OutboxMessage.Notification`, доставит обработчик шага 14) — в той же транзакции.
- **`POST /api/moderation/reports/{id}/resolve`** `{ status, resolution }` — закрытие жалобы (`Resolved`/`Rejected`).
- **`POST /api/moderation/users/{id}/ban`** `{ reason, until? }` — **в одной транзакции**: `IsBanned`,
  `BannedUntil`, **ротация `SecurityStamp`** (иначе выданный access-токен жил бы ещё до 15 мин), все активные
  объявления `→ Archived`, **все refresh-токены отозваны**, запись в журнал. После коммита — `Invalidate`
  кэша снимка (бан действует немедленно). Нельзя забанить себя (`400`) и администратора (`403`).
- **`POST /api/moderation/users/{id}/unban`** — снятие бана (+ сброс кэша снимка).
- **`GET /api/moderation/users/{id}`** — **email и телефон**. Самая чувствительная ручка: **каждый вызов**
  (даже просмотр) пишется в `moderation_logs`. Покрыта отдельным тестом на доступ и на запись в журнал.
- **`GET /api/moderation/stats`** — счётчики очереди и активности за сегодня/неделю.

- **`moderation_logs` — таблица ТОЛЬКО на добавление** (`ModerationLog`, `IModerationAudit`): ни `UPDATE`, ни
  `DELETE` в коде. Actor берётся из текущего пользователя, запись добавляется в тот же `DbContext` и коммитится
  **в одной транзакции** с действием. Пишется каждое действие модератора **и** просмотр контактов пользователя.
- Тесты (Testcontainers): обычный пользователь → `403` на всех ручках; аноним → `401`; очередь (автофлаги
  раньше, фильтр по причине); approve/reject (+outbox +лог); resolve жалобы; **бан** (архивация объявлений,
  невидимость в каталоге, `401` на создание объявления и на refresh); unban; контакты (PII + лог на КАЖДЫЙ вызов).

**Почему именно так:**
- **Ротация `SecurityStamp` в бане обязательна** — access-токен живёт до 15 мин; без смены штампа забаненный
  ещё продолжал бы работать. `SecurityStampValidator` кэширует снимок (TTL), поэтому после коммита — `Invalidate`.
- **Единая очередь из двух таблиц без отдельной таблицы очереди** — объявления и жалобы приводятся к общей
  строке `QueueRow`, keyset применяется к каждому источнику, слияние и курсор — по кортежу `(priority, createdAt, id)`.
- **Уведомление через Outbox, а не прямая отправка** — обработчик уведомлений (шаг 14) ещё не построен; reject
  лишь кладёт сообщение в той же транзакции, что и смена статуса (не теряется, не блокирует HTTP).
- **Роль только из claim** — соответствует инварианту авторизации (политики `Moderator`/`Admin` из шага 5).

**Ключевые файлы:** `Api/Controllers/ModerationController.cs`, `Api/Contracts/ModerationDtos.cs`,
`Api/Moderation/{ModerationAudit,ModerationCursor,ModerationServiceCollectionExtensions}.cs`,
`Domain/Entities/{ModerationLog,OutboxMessage}.cs`,
`Infrastructure/Persistence/Configurations/ModerationLogConfiguration.cs`,
`Infrastructure/Persistence/Migrations/*_AddModerationLog.cs`, тесты `tests/GenesisMarket.Tests/ModerationTests.cs`.

---

### 15. Гигиена каталога (автоархивация, поднятие, продажа, восстановление)

**Что сделано:**
- **Все переходы жизненного цикла — только доменными методами `Listing`** (`Publish`, `Bump`, `Archive`,
  `MarkArchiveWarned`, `MarkSold`, `ReactivateFromSold`, `RestoreFromArchive`); контроллер больше не пишет
  `listing.Status = …` напрямую. Новые метки: `BumpedAt`, `ArchivedAt`, `SoldAt`, `ArchiveWarningAt`.
- **Автоархивация (Quartz, раз в сутки).** `Active` с `coalesce(BumpedAt, PublishedAt, CreatedAt)` старше
  `ArchiveAfterDays` (30) → `Archived`. За `WarnBeforeDays` (3) дня до этого автору уходит уведомление
  через Outbox со ссылкой «продлить» (поднятие). Срок — в конфигурации (`CatalogHygiene`).
- **`POST /api/listings/{id}/bump`** — обновляет `BumpedAt` (объявление всплывает в каталоге). Бесплатно,
  не чаще раза в `BumpCooldownDays` (7). Лимит проверяется **под блокировкой строки** (`SELECT … FOR UPDATE`
  в транзакции) — два параллельных запроса не поднимут дважды; превышение лимита → `429` с `Retry-After`.
- **Сортировка каталога по умолчанию — `coalesce(BumpedAt, PublishedAt, CreatedAt) DESC`** (токен `bumped`;
  `sort=new` остаётся отдельной опцией по `CreatedAt`). Частичный индекс по выражению под keyset-пагинацию.
- **`POST /api/listings/{id}/mark-sold`** — `Active → Sold`. Объявление остаётся по прямой ссылке (там отзывы
  и история), но уходит из каталога и получает `noindex` на фронте (выводится из `status ≠ Active`). Обратный
  переход `Sold → Active` (**`/reactivate`**) разрешён в течение `SoldReactivationDays` (7) дней.
- **`POST /api/listings/{id}/restore`** — `Archived → Active`, если с архивации прошло ≤ `RestoreWithinDays`
  (90). Если автор получал `reject` за последние `RejectLookbackDays` (30) дней (по `moderation_logs`) —
  восстановление проходит **премодерацию заново** (`→ PendingReview`).
- **`daysUntilArchive`** в `ListingResponse` — сколько дней до автоархивации (только для `Active`).
- **Джоб:** `[DisallowConcurrentExecution]`, **persistent job store в PostgreSQL** (таблицы `qrtz_*` заведены
  этой же миграцией), **идемпотентность** — повторный прогон на тех же данных ничего не меняет (архивные уже
  не `Active`, предупреждённые помечены `ArchiveWarningAt`). Логика вынесена в `ICatalogHygieneService`
  (батчами) — джоб лишь вызывает её; в тестах сервис прогоняется напрямую (планировщик выключен
  `Scheduling:Enabled=false`).

**Почему именно так:**
- **`coalesce(BumpedAt, PublishedAt, CreatedAt)` вместо «сырого» `BumpedAt DESC`** — у не поднимавшихся и у
  одобренных модератором объявлений `BumpedAt = NULL`; откат на `PublishedAt`/`CreatedAt` делает и сортировку,
  и отсчёт срока корректными без правки чужих веток (одобрение объявления не трогаем).
- **Блокировка строки на bump, а не оптимистичный чек** — лимит «раз в 7 дней» должен держаться при гонке;
  `FOR UPDATE` сериализует конкурентные запросы по одному объявлению.
- **Флаг `ArchiveWarningAt` вместо «посчитать заново каждый прогон»** — гарантирует ровно одно предупреждение
  и идемпотентность; поднятие/восстановление его сбрасывают, чтобы продлённое объявление позже предупредили снова.
- **Переходы в доменной модели** — единая точка правды и инвариантов (нельзя поднять неактивное, вернуть
  непроданное и т.д.); окна/премодерация (нужны запросы к БД) остаются в контроллере, но статус меняет только домен.
- **Quartz-схема через EF-миграцию** — таблицы планировщика версионируются в репозитории (правило «схема — только
  миграцией»), а не создаются ручным SQL на проде.

**Ключевые файлы:** `Domain/Entities/Listing.cs` (методы переходов),
`Infrastructure/Scheduling/{CatalogHygieneOptions,CatalogHygieneService,CatalogHygieneJob,SchedulingServiceCollectionExtensions}.cs`,
`Api/Controllers/ListingsController.cs` (bump/mark-sold/reactivate/restore), `Api/Listings/CatalogQueryBuilder.cs`
(сортировка по умолчанию), `Api/Contracts/ListingDtos.cs` (`daysUntilArchive`),
`Infrastructure/Persistence/Migrations/*_AddCatalogHygiene.cs` (колонки + индекс + `qrtz_*`),
тесты `tests/GenesisMarket.Tests/CatalogHygieneTests.cs`.

---

### 16. Транзакционный Outbox (доставка уведомлений и внешних побочных эффектов)

**Что сделано:**
- **Единая инфраструктура внешних отправок.** Раньше в `outbox_messages` был только `delete-object`
  (удаление объектов MinIO фоновым `BackgroundService`) и «сырые» `notification`, которые никто не доставлял.
  Теперь это полноценный транзакционный outbox: **типизированные сообщения**, диспетчер, ретраи, финализация,
  уборка. Продюсеры кладут сообщение в БД **в той же транзакции**, что и доменное изменение — никаких
  отправок email/Telegram прямо из обработчика запроса (иначе при откате транзакции уведомление уже ушло бы).
- **Схема (`OutboxMessage`, миграция `AddOutboxPipeline`).** Поля `Status` (`Pending|Processing|Done|Failed`,
  строкой), `NextAttemptAt` (время следующей попытки, дефолт `now()`), `Attempts`, `Error` (переименован из
  `LastError`), `Payload`, `CreatedAt`, `ProcessedAt`. Частичный индекс `ix_outbox_due` по `Status='Pending'`
  под горячий путь диспетчера. Бэкфилл существующих строк в миграции: обработанные и legacy-`notification`
  переведены в `Done`, непроцессенные `delete-object` остались `Pending` (у них есть совместимый обработчик).
- **Диспетчер (Quartz, раз в 10 с, батч 50).** Забирает готовые сообщения
  `SELECT … WHERE Status='Pending' AND NextAttemptAt ≤ now ORDER BY CreatedAt LIMIT 50 **FOR UPDATE SKIP LOCKED**`
  внутри одной транзакции — несколько инстансов/тиков не возьмут одно сообщение. Каждое отдаётся обработчику по
  `Type`; исход (`Done`/повтор/`Failed`) фиксируется до COMMIT.
- **Ретраи — экспоненциальная задержка `10с → 1м → 5м → 30м → 2ч`, максимум 5 попыток**, затем `Status=Failed`
  и запись в лог. Сообщение **не удаляется** — остаётся для разбора. Задержка **персистентна** (через
  `NextAttemptAt`), а не in-memory: переживает рестарт и не держит воркер занятым между попытками.
  `OutboxPermanentException` (битый payload, удалённый адресат, отсутствующий ресурс) → сразу `Failed`, без трат попыток.
- **Уборщик (Quartz, ежедневно).** Удаляет `Done` старше `Outbox:RetentionDays` (30). `Failed` не трогает.
- **Каналы — `INotificationChannel`, выбор по настройке пользователя** (`Profile.NotifyVia`, дефолт `Email`):
  почта (поверх существующего `IEmailSender` — SMTP или dev-лог) и Telegram (`ITelegramClient` — HTTP Bot API,
  либо dev-лог без токена). Telegram-ЛС требует `Profile.TelegramChatId`; без него канал деградирует к почте.
  Адресата и контент обработчик достаёт из БД по id — **в `Payload` только идентификаторы**, персональных данных нет.
- **Типы сообщений и продюсеры:** `listing-approved`/`listing-rejected` (модерация, шаг 14 — заменили «сырой»
  `notification`), `listing-expiring-soon` (гигиена каталога, шаг 15), `new-review` (создание отзыва, шаг 13),
  `delete-images` (удаление фото, шаг 10 — объединил парные `delete-object`), `listing-published` (пост в
  Telegram-канал — обработчик готов, продюсер появится на шаге 16 привязки Telegram).
- **PII в логах.** `PiiScrubber` вычищает email и телефоны из текста ошибки перед записью в `Error` и в лог
  (сообщение SMTP-сбоя часто содержит адрес получателя) — логи outbox не содержат email и телефонов.
- Тесты (Testcontainers): **откат транзакции создания объявления ⇒ сообщения в outbox нет**; диспетчер доставляет
  и закрывает `Done`; транзиентные сбои ретраятся (растёт `Attempts`, `NextAttemptAt` сдвигается) и после 5 попыток
  → `Failed`, при этом PII вычищены; permanent-ошибка → сразу `Failed`; `delete-images` реально удаляет объекты из
  хранилища.

**Почему именно так:**
- **Outbox, а не прямая отправка** — доставка внешнему сервису не может участвовать в транзакции БД; запись
  сообщения в той же транзакции + отдельная доставка гарантируют «уведомление ⟺ изменение зафиксировано» без
  двойной записи и без блокировки HTTP-запроса на медленный SMTP/сеть.
- **`FOR UPDATE SKIP LOCKED`** — корректная конкурентная выборка на нескольких инстансах без гонки и без ожидания
  на заблокированных строках.
- **Персистентный бэкофф через `NextAttemptAt`, а не in-memory Polly-wait** — пауза в 2 часа не должна держать
  поток воркера и обязана пережить рестарт; поэтому задержка хранится в строке, а не в памяти процесса. (Осознанное
  отклонение от буквального «Polly» в постановке в пользу транзакционно-корректного варианта.)
- **Идентификаторы в payload, контент — из БД на момент отправки** — минимум персональных данных «на диске»,
  и уведомление отражает актуальное состояние (напр. заголовок объявления), а не снимок момента постановки.
- **Обработчики в слое Api, диспетчер-интерфейс в Infrastructure** — доставка требует каналов/шаблонов (Api), а
  Quartz-джоб живёт в Infrastructure; джоб вызывает `IOutboxDispatcher` (интерфейс в Infrastructure, реализация в
  Api), как `CatalogHygieneJob` вызывает `ICatalogHygieneService`.

**Компромисс именования:** поле оставлено как `Payload` (не `PayloadJson` из постановки) — для `delete-images`
это JSON-массив ключей, а для legacy `delete-object` — «сырой» ключ, так что нейтральное имя точнее.

**Конфигурация:** секция `Outbox` (`DispatchIntervalSeconds`=10, `BatchSize`=50, `CleanupCron`, `RetentionDays`=30),
секция `Telegram` (`BotToken`, `BroadcastChatId` — только из env). Диспетчер и уборщик отключаются вместе с
планировщиком (`Scheduling:Enabled=false`, как в тестах — там `IOutboxDispatcher` прогоняется напрямую).

**Ключевые файлы:** `Domain/Entities/OutboxMessage.cs`, `Domain/Enums/Enums.cs` (`OutboxStatus`,
`NotificationChannel`), `Infrastructure/Outbox/IOutboxDispatcher.cs`,
`Infrastructure/Scheduling/{OutboxOptions,OutboxDispatchJob,OutboxCleanupJob,SchedulingServiceCollectionExtensions}.cs`,
`Api/Outbox/*` (`OutboxDispatcher`, `IOutboxHandler`+обработчики, `INotificationChannel`+каналы, `UserNotifier`,
`ITelegramClient`, `PiiScrubber`, `OutboxServiceCollectionExtensions`),
продюсеры `Api/Controllers/{ModerationController,ReviewsController,ListingImagesController}.cs` и
`Infrastructure/Scheduling/CatalogHygieneService.cs`,
`Infrastructure/Persistence/Migrations/*_AddOutboxPipeline.cs`, тесты `tests/GenesisMarket.Tests/OutboxTests.cs`.

> **Прод-миграция:** `AddOutboxPipeline` переименовывает `LastError→Error`, добавляет `Status`/`NextAttemptAt`
> в `outbox_messages` и `NotifyVia`/`TelegramChatId` в `profiles`. БД — прод, поэтому миграцию накатывает
> пользователь (`dotnet ef database update`), с `pg_dump` перед сменой схемы. Планировщик Quartz на старте
> инициализирует стор, поэтому прод не поднимется без применённой миграции (как и с `AddCatalogHygiene`).

### 17. Сохранённые поиски (возврат пользователей)

**Что сделано:**
- **`SavedSearch` (миграция `AddSavedSearches`).** Пользователь сохраняет набор фильтров каталога, а фоновый
  джоб находит по ним новые объявления и уведомляет автора. Поля: `UserId`, `Name`, `QueryJson` (**jsonb**),
  `LastNotifiedListingId?` (курсор), `LastRunAt`, `IsActive`, `NotifyChannel` (`Email|Telegram|None`, строкой),
  `NotifiedAt?`, `CreatedAt`. `QueryJson` хранит ровно те же параметры, что принимает `GET /api/listings`
  (`q, category, subcategory, cities[], priceFrom, priceTo, condition, priceType`) — без `sort/cursor/limit`
  (это параметры выдачи, а не критерии).
- **CRUD `POST/GET/PATCH/DELETE /api/saved-searches`** (все `[Authorize]`, владелец проверяется на сервере).
  Лимит **10 активных поисков** на пользователя. При сохранении/смене критериев/реактивации курсор
  **привязывается к самому свежему совпадению сейчас** (`SavedSearchQueryPlanner.AnchorAsync`) — подписчик
  получает только будущие объявления, а не рассылку по всему каталогу.
- **Единый билдер запроса.** Прогон и живой каталог отбирают объявления **одним и тем же**
  `CatalogQueryBuilder` (фильтры + FTS). Кросс-полевые проверки (`≤7 городов`, `priceFrom ≤ priceTo`) и
  нормализация `q` вынесены в `CatalogQueryBuilder` и переиспользуются контроллером каталога и сохранёнными
  поисками. **Критерии из jsonb не доверяются**: при сохранении и **при каждом прогоне** они заново
  десериализуются и валидируются; некорректный поиск джоб деактивирует (`IsActive=false`), не рассылая.
- **`SavedSearchNotificationJob` (Quartz, раз в 15 минут, `[DisallowConcurrentExecution]`, persistent store).**
  Батчами по 200 активных поисков (keyset по `Id`). Для каждого — тот же запрос каталога с дополнительным
  условием **по курсору `(PublishedAt, Id) > последнего уведомлённого`**, а не по времени: объявления с
  одинаковым `PublishedAt` не теряются и не дублируются между прогонами. Найдено больше нуля → **одно**
  Outbox-сообщение `saved-search-match` со списком (**до 10** объявлений), курсор и `NotifiedAt` двигаются вперёд.
- **Не чаще одного уведомления на поиск в час**, даже если джоб отработал чаще: поиск с `NotifiedAt` свежее часа
  джоб пропускает целиком (курсор не трогает — новые объявления не теряются, уедут следующим уведомлением).
- **Доставка — через тот же Outbox** (шаг 16): обработчик `SavedSearchMatchHandler` собирает письмо из БД по id
  и шлёт каналом **самого поиска** (`UserNotifier.NotifyViaAsync` — Telegram без `TelegramChatId` деградирует к
  почте). `None` — не рассылать.
- Тесты (Testcontainers): **два прогона подряд без новых объявлений дают ровно одно уведомление** (ключевой
  инвариант ТЗ); курсорная идемпотентность даже при открытом часовом гейте; отсрочка на час без потери; привязка
  курсора (объявления «до сохранения» не рассылаются); недоверие к jsonb (порча деактивирует); доставка
  диспетчером; CRUD, лимит 10, `>7` городов → 400, аноним → 401.

**Почему именно так:**
- **Курсор по `(PublishedAt, Id)`, а не по времени** — при равных `PublishedAt` временной порог либо пропустил бы
  объявление, либо прислал бы дубль; пара с тай-брейком по `Id` даёт строгий детерминированный порядок.
- **Привязка курсора при сохранении** — иначе первый же прогон нового поиска, совпавшего с сотнями существующих
  объявлений, завалил бы подписчика; уведомляем только о том, что появилось **после** сохранения.
- **Валидация из jsonb при каждом прогоне** — критерии в хранилище могли устареть/испортиться; каталог и джоб
  должны применять один и тот же набор правил, поэтому валидатор общий.
- **Сервис в Api, интерфейс/джоб в Infrastructure** — прогон использует `CatalogQueryBuilder` (Api); Quartz-джоб
  вызывает `ISavedSearchNotificationService` (интерфейс в Infrastructure, реализация в Api), как
  `OutboxDispatchJob → IOutboxDispatcher` и `CatalogHygieneJob → ICatalogHygieneService`.

**Конфигурация:** секция `SavedSearch` (`NotificationCron`=`0 0/15 * * * ?`, `BatchSize`=200,
`MaxListingsPerNotification`=10, `MinNotificationIntervalMinutes`=60, `MaxActivePerUser`=10). Джоб отключается
вместе с планировщиком (`Scheduling:Enabled=false`, как в тестах — там `ISavedSearchNotificationService`
прогоняется напрямую).

**Ключевые файлы:** `Domain/Entities/SavedSearch.cs`, `Domain/Enums/Enums.cs` (`SavedSearchNotifyChannel`),
`Infrastructure/Persistence/Configurations/SavedSearchConfiguration.cs`,
`Infrastructure/Scheduling/{SavedSearchOptions,ISavedSearchNotificationService,SavedSearchNotificationJob}.cs`,
`Api/SavedSearches/*` (`SavedSearchNotificationService`, `SavedSearchQueryPlanner`, `SavedSearchJson`,
`SavedSearchServiceCollectionExtensions`), `Api/Controllers/SavedSearchesController.cs`,
`Api/Contracts/SavedSearchDtos.cs`, `Api/Outbox/{OutboxHandlers,OutboxContracts,UserNotifier}.cs`
(`SavedSearchMatchHandler`), `Api/Listings/CatalogQueryBuilder.cs` (общие хелперы),
`Infrastructure/Persistence/Migrations/*_AddSavedSearches.cs`, тесты `tests/GenesisMarket.Tests/SavedSearchTests.cs`.

> **Прод-миграция:** `AddSavedSearches` добавляет таблицу `saved_searches` (аддитивно, без изменения данных).
> БД — прод, поэтому миграцию накатывает пользователь (`dotnet ef database update`). Новый Quartz-джоб
> регистрируется в сторе на старте — прод не поднимется без применённой миграции (как и с `AddCatalogHygiene`).

### 18. Публикация объявлений в Telegram-канал (дешёвый канал роста)

**Что сделано:**
- **Пост при переходе в Active.** Любой переход объявления в `Active` (создание сразу активным, `POST
  /listings/{id}/publish`, одобрение модератором, возврат из продажи/архива) ставит в Outbox сообщение
  `listing-published` **в той же транзакции**, что и смена статуса. Доставку выполняет обработчик
  `ListingPublishedHandler` (шаг 16), отдельно от HTTP-запроса, с ретраями.
- **Формат поста (plain text).** Заголовок, цена в рублях ПМР, город и категория (русские подписи из
  `CatalogLabels`), абсолютная ссылка на карточку (`Telegram:WebBaseUrl` + `/listing/{slug}`). **`parse_mode`
  не используется вовсе** — заголовок/описание пишет пользователь, надёжно экранировать разметку под MarkdownV2
  (18 спецсимволов) не стоит труда; проще отправлять без разметки.
- **Фото или текст.** Есть изображение → `sendPhoto` первого по порядку (presigned-URL из MinIO, TTL 1 ч);
  нет фото → `sendMessage`. Если Telegram не смог обработать картинку (формат/размер/URL) — анонс **не теряем**,
  публикуем текстом (`TelegramApiException` → фолбэк на `sendMessage`).
- **Маршрутизация «категория → канал».** Словарь `Telegram:CategoryChannels` (`category → chatId`) с откатом
  на общий `Telegram:BroadcastChatId`. `message_id` **и chatId** поста сохраняются в `Listing`
  (`TelegramMessageId`, `TelegramChatId`, миграция `AddListingTelegramPost`) — чтобы позже отредактировать
  именно тот пост в том канале.
- **Пометки «Продано»/«Снято».** `mark-sold` и снятие/архивация (в т.ч. **авто-архивация** гигиеной каталога)
  ставят `listing-channel-update` → `editMessageCaption` (у поста-фото) или `editMessageText` (у текстового)
  с шапкой «✅ ПРОДАНО» / «⛔ Снято с публикации». Возврат в продажу/из архива — обратная правка на «чистую»
  подпись. **Если пост удалён вручную — не падаем**: Telegram-ошибки «message to edit not found / can't be
  edited» трактуются как «править нечего» (обработчик завершается успешно).
- **Идемпотентность.** `listing-published` при уже существующем `TelegramMessageId` не постит повторно, а правит
  подпись на чистую (сценарий повторной активации Sold/Archived → Active).
- **Лимит частоты — проактивно.** `SlidingWindowTelegramRateLimiter` (singleton) держит ≤ **20 сообщений в
  минуту на канал** скользящим окном **до** отправки, а не реагируя на 429. При всплеске публикаций ждёт
  освобождения слота до `MaxRateLimitWaitMs`, дальше — откладывает отправку (сообщение вернётся в очередь
  Outbox), чтобы не держать транзакцию диспетчера.
- **Ретраи через Polly.** 429 → задержка из `parameters.retry_after` тела ответа (с откатом на заголовок
  `Retry-After`); сеть/5xx → экспонента `2/4/8/16 c`. Постоянные 4xx (`TelegramApiException`) не ретраятся —
  их разбирает обработчик. Исчерпание Polly передаёт сбой наверх — там уже персистентные ретраи Outbox.
- Тесты (Testcontainers): пост в канал категории с сохранением `message_id`; `sendPhoto` при наличии фото;
  откат на общий канал для категории без своего; правка «Продано»; no-op при отсутствии поста; правка вместо
  повторного поста при реактивации; полный путь `approve → пост`; модульные тесты лимитера (потолок и
  независимость по каналам).

**Почему именно так:**
- **Токен и id каналов — только env.** Секреты не в конфиге репозитория; `Telegram:BotToken` пуст ⇒
  `LogTelegramClient` пишет намерение в лог (dev-фолбэк, как у почты), реальная сеть не трогается.
- **chatId поста в БД, а не только message_id** — при маршрутизации по категориям пост живёт в конкретном
  канале; чтобы его отредактировать позже, нужно знать и канал.
- **Лимит на стороне обработчика, а не через 429** — упираться в блокировку API и надеяться на ретраи дороже и
  медленнее, чем заранее разложить отправку по окну; Polly остаётся страховкой на редкий 429.
- **Фолбэк фото → текст** — картинка в WebP/по URL может не пройти обработку Telegram; терять анонс (главный
  смысл фичи — рост) из-за этого нельзя, текстовый пост лучше отсутствия.

**Конфигурация:** секция `Telegram` (`BotToken`, `BroadcastChatId`, `WebBaseUrl`, `CategoryChannels` —
словарь `category→chatId`, `MaxMessagesPerMinutePerChat`=20, `MaxRateLimitWaitMs`=5000; секреты — только env).

**Ключевые файлы:** `Domain/Entities/Listing.cs` (`TelegramChatId/MessageId`, `AttachChannelPost`),
`Domain/Entities/OutboxMessage.cs` (`ListingPublished`, `ListingChannelUpdate`),
`Api/Outbox/Telegram/*` (`TelegramOptions`, `ITelegramClient`, `HttpTelegramClient`, `LogTelegramClient`,
`TelegramRateLimiter`, `TelegramExceptions`, `TelegramPostFormatter`), `Api/Listings/CatalogLabels.cs`,
`Api/Outbox/{OutboxHandlers,OutboxContracts,NotificationChannels,OutboxServiceCollectionExtensions}.cs`,
продюсеры `Api/Controllers/{ListingsController,ModerationController}.cs` и
`Infrastructure/Scheduling/CatalogHygieneService.cs`,
`Infrastructure/Persistence/Migrations/*_AddListingTelegramPost.cs`,
тесты `tests/GenesisMarket.Tests/{TelegramPublishTests,CapturingTelegramClient}.cs`.

> **Прод-миграция:** `AddListingTelegramPost` добавляет `TelegramChatId`/`TelegramMessageId` в `listings`
> (аддитивно, nullable, без изменения данных). БД — прод, миграцию накатывает пользователь
> (`dotnet ef database update`), с `pg_dump` перед сменой схемы.

---

### 19. Подготовка к индексации: мета, sitemap, robots, посадочные (органический трафик)

**Что сделано:**
- **`GET /api/listings/{id}/meta`** — готовые данные для `<head>` карточки: `title`, `description`,
  `canonicalUrl` (`/obyavlenie/{slug}`), og-теги (`ogTitle`, `ogDescription`, `ogImage`) и **JSON-LD
  `schema.org/Product`** с `offers` (`price`, `priceCurrency`, `availability`, `itemCondition`, `seller`).
  `ogImage` — presigned-ссылка на первое фото с **длинным TTL** (`Seo:OgImageTtlDays`, по умолчанию 7 дней):
  og-картинку кэшируют соцсети/поисковики, короткий TTL давал бы «битые» превью в выдаче.
- **Валюта — `RUP`.** У рубля ПМР нет кода ISO-4217. В JSON-LD `priceCurrency` = **`RUP`** (не `RUB`/`MDL` —
  это другие валюты), а человеку валюта поясняется **текстом** в `description` («Цена указана в рублях ПМР (RUP)»).
  Для договорной цены (`Negotiable`) поле `price` в offers **опускается** (пустую цену schema.org не любит).
- **HTTP-коды под судьбу URL в индексе.** Удалённое (soft-delete) → **410 Gone** (поисковик убирает URL из
  индекса); снятое с публикации (`Archived`/`Sold`) → **200** с `isArchived: true` и `noIndex: true`
  (фронт ставит `<meta name=robots content=noindex>`, но URL остаётся); черновик/премодерация/отклонённое
  (публичного URL не было) и несуществующее → **404**. Разница 410↔404 намеренная: 410 говорит убрать URL, 404 — нет.
- **`canonicalUrl` в DTO объявления.** `GET /api/listings/{id}` (и `by-slug`, «мои», и т.д.) всегда несут
  `canonicalUrl` — единый канонический адрес для `<link rel=canonical>` и шеринга.
- **`GET /sitemap.xml`** — главная, все категории, все города, все `Active` объявления. Пока URL ≤ порога
  (`Seo:SitemapSplitThreshold`=45 000) — один `<urlset>`; больше — **sitemap-index** с разбивкой по
  `Seo:SitemapPageSize`=40 000 (`/sitemap-static.xml` + `/sitemap-listings-{n}.xml`). Генерация **потоковая**
  (`IAsyncEnumerable` из EF прямо в тело ответа через `XmlWriter`) — весь список в память не материализуется.
  Число активных объявлений кэшируется на час; ответы отдаются с `Cache-Control: public, max-age=3600`.
- **`GET /robots.txt`** — закрывает служебные API (`/api/moderation/`, `/api/me/`, `/api/auth/`) и указывает
  `Sitemap:`. Каталог и карточки остаются открыты.
- **`GET /api/seo/landing/{category}/{city}`** — данные для статических посадочных «Купить квартиру в
  Тирасполе»: счётчик активных объявлений, диапазон цен (`priceFrom`/`priceTo` по объявлениям с ценой) и топ
  подкатегорий (по числу объявлений). Неизвестная пара категория/город → 404.
- Тесты (Testcontainers): мета Active (canonical/og/JSON-LD/RUP/InStock), договорная цена без `price`,
  архив/продано (200 + noindex + Discontinued/SoldOut), 410 для удалённого, 404 для черновика/несуществующего,
  `canonicalUrl` в DTO, robots, sitemap (urlset + loc объявления + Cache-Control), посадочная и её 404.

**Почему именно так:**
- **Мета собирает сервер, а не фронт** — SSR/краулер получает готовые title/description/JSON-LD, логика
  формирования (валюта, availability, обрезка описания) не дублируется и не расходится с бэкендом.
- **Потоковый sitemap** — объявлений могут быть сотни тысяч; `ToListAsync` на весь каталог держал бы память и
  задерживал первый байт. XML пишется по мере чтения курсора БД.
- **410 vs 200-noindex** — разное намерение: удалённое надо стереть из индекса (410), снятое с публикации может
  вернуться (200 + noindex сохраняет URL «на паузе»).
- **`Seo:WebBaseUrl` пуст ⇒ 503.** Без публичного адреса индексировать нечего и абсолютные ссылки не построить;
  `canonicalUrl` в DTO объявления в этом случае `null` (dev), эндпоинты мета/sitemap/посадочных — 503.

**Конфигурация:** секция `Seo` (`WebBaseUrl` — только env, обычно = адресу фронтенда; `SiteName`,
`OgImageTtlDays`=7, `SitemapSplitThreshold`=45000, `SitemapPageSize`=40000, `SitemapCacheSeconds`=3600).

**Ключевые файлы:** `Api/Seo/*` (`SeoOptions`, `SeoUrls`, `ListingMetaBuilder`, `SeoServiceCollectionExtensions`),
`Api/Controllers/{SeoController,SitemapController}.cs`, `Api/Contracts/SeoDtos.cs`,
`Api/Contracts/ListingDtos.cs` (`CanonicalUrl`), `Api/Controllers/ListingsController.cs` (проброс canonical),
тесты `tests/GenesisMarket.Tests/SeoTests.cs`.

> **Прод:** миграций нет (только чтение). Перед запуском задать `SEO_WEB_BASE_URL` (env) — иначе SEO-эндпоинты
> отдают 503, а `canonicalUrl` в DTO объявлений приходит `null`.

---

## Известные ограничения

- **Сид `subcategories` — провизорный.** Источник правды `pmr_market_prompt.md` (раздел CATEGORIES)
  в репозитории отсутствует; текущие 42 подкатегории — заглушка, заменить на реальный список.
- **`FirstImageUrl` в карточке каталога — это `ThumbKey` (ключ объекта), а не presigned URL.** Загрузка и
  обработка фото уже есть (фича 10), но проекция каталога пока отдаёт ключ; подписывать ссылки в списковой
  выдаче (батчем) — отдельный шаг, чтобы не генерировать presigned на каждую карточку синхронно.
- **`IsBumped` в карточке каталога** = объявление хотя бы раз поднимали после публикации (`BumpedAt > PublishedAt`).
  Продвижение бесплатное (`POST /listings/{id}/bump`, фича 15); платного промо в MVP нет.
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
- **Telegram-доставка личных уведомлений — привязка ЛС нет.** Транзакционный outbox доставляет уведомления
  (фича 16); **личный** канал Telegram работает только при заполненном `Profile.TelegramChatId`, а флоу его
  привязки (бот, `/start`, сохранение chat_id) ещё не построен. До него `NotifyVia=Telegram` без chat_id
  деградирует к почте. **Публикация объявлений в каналы (фича 18) от этого не зависит** — она шлёт в каналы по
  их chatId из конфигурации. Без `Telegram:BotToken` Telegram-клиент пишет в лог (`[DEV TELEGRAM] …`), как
  dev-фолбэк почты.
- **Разбан не восстанавливает объявления.** Бан архивирует активные объявления, но `unban` их обратно в `Active`
  не переводит — владелец публикует заново. Осознанное решение: авто-восстановление рискует вернуть в каталог то,
  что и было причиной бана.
- **Rate-limit жалоб — in-memory** (на инстанс), как и остальные лимиты. При нескольких инстансах API нужен общий стор (Redis).
- **SEO-пути фронтенда — договорённость, не автоматика.** Бэкенд отдаёт канонические ссылки под конкретную
  маршрутизацию фронта: карточка `/obyavlenie/{slug}`, категория `/{category}`, город `/city/{city}`, посадочная
  `/{category}/{city}` (значения — как в БД: `realestate`, `tiraspol`). Фронт обязан обслуживать эти URL; при
  смене схемы путей — синхронно править `Api/Seo/SeoUrls.cs`. Пост Telegram-канала пока ссылается на **старый**
  путь `/listing/{slug}` (`TelegramPostFormatter`) — свести к `/obyavlenie/{slug}` отдельным шагом.
- **`Seo:WebBaseUrl` дублирует `Telegram:WebBaseUrl`.** Обычно это один и тот же адрес фронтенда, но заданы
  двумя переменными (`SEO_WEB_BASE_URL`, `TELEGRAM_WEB_BASE_URL`). Позже разумно свести к общей секции `Site`.
- **Sitemap объявлений — offset-пагинация** (`Skip/Take` по `Id`). Для сотен тысяч глубокие страницы дают рост
  стоимости `OFFSET`; ответы кэшируются на час и запрашиваются краулером редко, так что приемлемо. При кратном
  росте каталога перейти на keyset по `Id`.
