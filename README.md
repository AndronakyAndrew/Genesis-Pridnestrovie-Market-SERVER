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

# 3. Создать бакет MinIO (пока вручную — см. «Известные ограничения»).
docker exec <minio-container> mc alias set gm http://localhost:9000 <MINIO_ROOT_USER> <MINIO_ROOT_PASSWORD>
docker exec <minio-container> mc mb -p gm/listings

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

## Известные ограничения

- **Сид `subcategories` — провизорный.** Источник правды `pmr_market_prompt.md` (раздел CATEGORIES)
  в репозитории отсутствует; текущие 42 подкатегории — заглушка, заменить на реальный список.
- **Бакет MinIO `listings` не создаётся автоматически** — сейчас создаётся вручную (шаг 3 запуска).
  TODO: init-контейнер `mc` или «ensure bucket» при старте API.
- **SMS — заглушка `DevSmsSender`** (код в лог `[DEV SMS] …`). Подтверждение телефона доступно,
  но на проде не гейтит публикацию (гейт = почта). Реальная отправка — вторая реализация `ISmsSender`
  (SMS-провайдер или Telegram/Viber-бот).
- **Почта через `System.Net.Mail.SmtpClient`** — базовый вариант на старте; при пустом `Smtp:Host`
  код пишется в лог (`[DEV EMAIL] …`). Позже разумно перейти на MailKit.
- **Блок-лист паролей — курируемое подмножество**, не полный top-1000. Расширить `Auth/common-passwords.txt`.
- **Rate-limit — in-memory** (на инстанс). При нескольких инстансах API нужен общий стор (напр. Redis).
