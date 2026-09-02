# Dev-окружение Genesis Market

Изолированный docker-compose стек для локальной разработки backend (ASP.NET
Core API). Полностью независим от production: свои контейнеры, порты, БД,
volumes, файл окружения. Файлы: `docker-compose.dev.yml`, `.env.dev`,
`Makefile`.

## Порты (dev, отличаются от prod)

| Сервис   | URL с хоста              |
|----------|---------------------------|
| API      | http://localhost:8091      |
| Adminer  | http://localhost:8082      |
| Postgres | localhost:5435              |
| MinIO S3 | http://localhost:9002      |
| MinIO Console | http://localhost:9003 |

## Первый запуск

1. Скопировать шаблон переменных окружения и при желании поправить значения
   (дефолтные dev-креды из шаблона тоже подойдут — это не прод):
   ```bash
   cp .env.dev.example .env.dev
   ```
2. Поднять стек:
   ```bash
   make dev-up
   ```
   (без `make` — `docker compose -f docker-compose.dev.yml --env-file .env.dev up -d --build`)

   Первый запуск дольше обычного: сервис `migrator` ставит `dotnet-ef` и
   накатывает все EF-миграции на чистую БД, только потом стартует `api`.
3. Проверить, что всё поднялось:
   ```bash
   curl http://localhost:8091/health/live
   ```
   Должно вернуть `200`. Логи — `make dev-logs`.

Hot reload включён (`dotnet watch`) — правки в `src/` подхватываются
автоматически, пересобирать контейнер вручную не нужно.

## Команды Makefile

| Команда            | Что делает |
|--------------------|------------|
| `make dev-up`      | Поднять стек (api, postgres, minio, adminer) |
| `make dev-down`    | Остановить контейнеры, данные в volumes остаются |
| `make dev-logs`    | Логи всех сервисов (`Ctrl+C` — выйти из просмотра) |
| `make dev-rebuild` | Пересобрать `api` с нуля: чистит закешированные `bin`/`obj`, пересоздаёт контейнер. Нужно после смены `.csproj` / добавления NuGet-пакета — обычные правки кода подхватывает hot reload сам |
| `make dev-reset`   | Снести volumes dev (БД, файлы MinIO) — **необратимо для dev-данных**, дальше `dev-up` поднимет всё с чистого листа |

Windows: GNU `make` из коробки нет — либо `choco install make`, либо запускай
команды `docker compose ...` из `Makefile` напрямую (без `make`).

## Подключение к БД через Adminer

1. Открыть http://localhost:8082
2. Заполнить форму входа:
   - **System**: PostgreSQL
   - **Server**: `postgres` (имя сервиса в docker-сети, не `localhost`)
   - **Username** / **Password** / **Database**: значения `POSTGRES_USER` /
     `POSTGRES_PASSWORD` / `POSTGRES_DB` из своего `.env.dev`

Через `psql` с хоста (порт `5435`, не 5432):
```bash
psql "postgresql://<POSTGRES_USER>:<POSTGRES_PASSWORD>@localhost:5435/<POSTGRES_DB>"
```
(готовая строка уже лежит в `.env.dev` как `DATABASE_URL_HOST`).

## Работа с MinIO

- **Web-консоль**: http://localhost:9003 — логин/пароль это
  `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` из `.env.dev`.
- **S3 API endpoint** (для инструментов типа `mc`, `aws s3` с кастомным
  endpoint, или для отладки того, что видит сам api): `http://localhost:9002`.
- Бакет, который использует api — значение `MINIO_BUCKET` из `.env.dev`
  (по умолчанию `genesis-dev`). Api создаёт бакет сам при старте, если его
  ещё нет — руками создавать не обязательно.
- Файлы живут в volume `genesis-dev-minio` — `make dev-down` их не трогает,
  `make dev-reset` удаляет.

## Чего делать нельзя

- **Не трогать prod из dev-окружения.** `docker-compose.dev.yml` полностью
  изолирован (свои контейнеры `*-dev`, сеть `genesis-dev`, volumes
  `genesis-dev-*`, порты 8091/8082/5435/9002/9003) — но это не защита от
  человеческих ошибок:
  - не запускай `docker compose -f docker-compose.yml ...` (без `.dev`) на
    этой машине — это prod-файл, конфигурация из `.env` (не `.env.dev`);
  - на «голый» `docker compose up` (без `-f`) в этой папке теперь стоит
    guard в `docker-compose.yml` — без явного `I_CONFIRM_PROD_LAUNCH=yes`
    перед командой он откажет с понятной ошибкой вместо тихого запуска
    прода. Для dev эта переменная не нужна вообще — `docker-compose.dev.yml`
    её не требует;
  - не путай `.env` (прод) и `.env.dev` (dev) — они относятся к разным
    compose-файлам и разным базам;
  - `make dev-reset` необратимо удаляет **dev**-volumes; имя команды и
    вывод `docker compose ps` всегда содержат суффикс `-dev` — если его
    нет, это не dev-контейнер, дважды проверь, прежде чем сносить.
- **Не коммитить `.env.dev`** — он в `.gitignore` (реальные, пусть и
  фиктивные dev-креды, туда не попадают). Менять стоит только
  `.env.dev.example` (шаблон, без значений).
- **Не полагаться на dev-креды нигде за пределами localhost.** Пароли в
  `.env.dev.example`/`.env.dev` — заведомо простые и одинаковые для всех,
  кто клонирует репозиторий; для прода они не подходят и не должны туда
  попадать ни в каком виде.
- **Не считать `dev-rebuild`/`dev-reset` безопасными на общих машинах.**
  Эти команды трогают только volumes и контейнеры с суффиксом `-dev`
  проекта `genesis-market-dev`, но если на серверном ноутбуке когда-либо
  запускался `docker-compose.dev.yml` — не гоняй `dev-reset` там не глядя.
