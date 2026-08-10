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
9. **Все даты — UTC**, `DateTimeOffset` в коде, `timestamptz` в БД.

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

## Известные ограничения

- **Сид `subcategories` — провизорный.** Источник правды `pmr_market_prompt.md` (раздел CATEGORIES)
  в репозитории отсутствует; текущие 42 подкатегории — заглушка, заменить на реальный список.
- **Бакет MinIO `listings` не создаётся автоматически** — сейчас создаётся вручную (шаг 3 запуска).
  TODO: init-контейнер `mc` или «ensure bucket» при старте API.
- **JWT ещё не подключён** — авторизация в контроллерах пока опирается на claim из `CurrentUserId()`
  (полноценный JWT-мидлвар — следующая фича).
