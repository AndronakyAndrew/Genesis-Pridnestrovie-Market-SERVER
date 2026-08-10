# Genesis Market — Server (Backend)

Каркас серверного приложения маркетплейса объявлений Genesis Market.
Первый проект экосистемы ООО «Genesis Industries Corp».

Claude Code читает этот файл автоматически. Не удалять.

## Стек

- **Runtime**: ASP.NET Core, .NET 10 LTS
- **БД**: PostgreSQL 17, EF Core 10 + Npgsql
- **Хранилище файлов**: MinIO (S3-совместимое)
- **Логи**: Serilog → структурированный JSON в stdout
- **Деплой**: Docker Compose (api, postgres, minio, adminer)

> В исходном ТЗ финальный образ был указан как `aspnet:9.0-alpine`.
> Это несовместимо с .NET 10, поэтому используется `10.0-alpine`.

## Структура решения

Четыре проекта, зависимости направлены внутрь (Api → Infrastructure → Domain):

```
SERVER/
  GenesisMarket.sln
  Directory.Build.props        — общие свойства (net10.0, nullable, warnings-as-errors)
  Dockerfile                   — многоступенчатая сборка API
  docker-compose.yml           — api + postgres + minio + adminer
  .env.example                 — все переменные окружения (значения пустые)

  src/
    GenesisMarket.Domain/          — сущности, enum-ы, доменные правила. БЕЗ зависимостей.
      Common/BaseEntity.cs
      Entities/                    — User, Listing, ...
      Enums/Enums.cs

    GenesisMarket.Infrastructure/  — DbContext, миграции, репозитории, внешние сервисы
      Persistence/                 — AppDbContext, конфигурации, design-time factory
      Storage/                     — MinIO: клиент, IObjectStorage, health check
      DependencyInjection.cs       — AddInfrastructure(): БД, MinIO, health checks

    GenesisMarket.Api/             — контроллеры, middleware, DI, конфигурация
      Program.cs                   — вся сборка приложения
      Controllers/                 — ApiControllerBase + контроллеры фич
      Contracts/                   — DTO (record) запросов/ответов
      Middleware/                  — GlobalExceptionHandler, RequestIdEnricher
      appsettings*.json

  tests/
    GenesisMarket.Tests/           — интеграционные тесты (WebApplicationFactory, Testcontainers)
```

## Соглашения об именовании

- **Проекты / namespace**: `GenesisMarket.<Layer>` (`GenesisMarket.Api`, `.Domain`, ...).
- **Сущности**: единственное число, PascalCase (`Listing`, `User`, `Order`).
- **Таблицы БД**: множественное число, snake_case (`listings`, `users`) — задаётся в `IEntityTypeConfiguration`.
- **DTO**: суффикс по назначению — `...Request` (вход), `...Response` (выход). Тип — `record`.
- **Контроллеры**: множественное число + `Controller` (`ListingsController`), наследуют `ApiControllerBase`.
- **Enum в БД**: хранятся строками (`.HasConversion<string>()`), не числами.
- **Даты**: только `DateTime.UtcNow`, столбцы `timestamptz`.
- **Ключи**: `Guid` (UUIDv7 через `Guid.CreateVersion7()`).
- **Конфигурация**: секции `Postgres`, `Minio`, `Cors`, `Serilog`; env-override через `Секция__Ключ`.

## Три обязательных правила

1. **Сущности EF никогда не используются как параметр или тип возврата контроллера.**
   Наружу и внутрь — только DTO из `Contracts/`. Маппинг руками в статическом методе.

2. **Каждый эндпоинт, принимающий id ресурса, проверяет владельца на сервере.**
   Явно в экшене: `if (entity.OwnerId != CurrentUserId()) return Forbid();`
   Не полагаться на скрытие кнопок на фронте.

3. **Любое изменение схемы — только миграцией EF Core, не SQL руками.**
   `dotnet ef migrations add <Имя> -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api`
   Миграции коммитятся в репозиторий.

## Инфраструктурные решения (уже в каркасе)

- **Serilog**: JSON (`RenderedCompactJsonFormatter`) в консоль, обогащение `RequestId`
  (middleware кладёт `TraceIdentifier` в `LogContext` и в заголовок `X-Request-Id`).
  Уровень — из конфигурации (`Serilog:MinimumLevel:Default`, env `Serilog__MinimumLevel__Default`).
- **Health checks**: `/health/live` (liveness, без зависимостей),
  `/health/ready` (readiness: Postgres + MinIO по тегу `ready`).
- **Swagger**: только в Development. В Production не регистрируется вовсе.
- **Обработка ошибок**: `IExceptionHandler` → `ProblemDetails` (RFC 7807).
  В Production в ответе только `traceId`; стектрейсы, тексты SQL и имена констрейнтов
  не раскрываются. Детали — в логах по `traceId`.
- **CORS**: origin-ы из конфигурации (`Cors:AllowedOrigins`, строка через запятую).
  `AllowAnyOrigin` не используется.
- **Секреты**: только через переменные окружения / `.env`. `.env` в `.gitignore`.

## Команды

```bash
# сборка и тесты
dotnet build
dotnet test

# миграции
dotnet ef migrations add <Имя> -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api
dotnet ef database update       -p src/GenesisMarket.Infrastructure -s src/GenesisMarket.Api

# локальный запуск всей инфраструктуры
cp .env.example .env            # заполнить значения
docker compose up --build
```
