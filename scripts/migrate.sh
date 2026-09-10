#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — прогон миграций EF Core как ОТДЕЛЬНОГО шага деплоя.
#
#  Приложение миграции при старте не накатывает намеренно: иначе роль, под
#  которой оно работает, обязана была бы постоянно иметь права на DDL. Здесь
#  DDL выполняется разово, под POSTGRES_USER (владелец схемы), в одноразовом
#  контейнере с SDK — рантайм-образ EF-инструментов не содержит.
#
#  Порядок деплоя:
#    1. ./scripts/backup.sh                       # копия перед изменением схемы
#    2. ./scripts/migrate.sh                      # схема
#    3. ./scripts/create-app-role.sql (при первом запуске и после новых таблиц)
#    4. I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --build api
#
#  Использование:
#    ./scripts/migrate.sh              # накатить до последней миграции
#    ./scripts/migrate.sh <MigrationId>   # откатиться/накатить до конкретной
# ============================================================================
set -euo pipefail

# Каталог скриптов фиксируем до cd: COMPOSE_DIR можно нацелить на другой стек,
# а load-env.sh лежит рядом с этим файлом.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
COMPOSE_DIR="${COMPOSE_DIR:-$(cd "$SCRIPT_DIR/.." && pwd)}"
cd "$COMPOSE_DIR"

# .env — источник значений по умолчанию, но НЕ поверх уже заданных переменных:
# так можно нацелить скрипт на конкретный стек (например, проверочный),
# не редактируя продовый .env:
#   POSTGRES_DB=... GENESIS_NETWORK=... ./scripts/migrate.sh
# shellcheck source=scripts/load-env.sh
source "$SCRIPT_DIR/load-env.sh"
load_env .env

PG_DB="${POSTGRES_DB:?POSTGRES_DB не задан (.env)}"
PG_USER="${POSTGRES_USER:?POSTGRES_USER не задан (.env)}"
PG_PASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD не задан (.env)}"
SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0-alpine}"
EF_VERSION="${EF_VERSION:-10.0.0}"
TARGET="${1:-}"

# Сеть стека: имя проекта Compose по умолчанию — имя каталога в нижнем регистре.
NETWORK="${GENESIS_NETWORK:-$(basename "$COMPOSE_DIR" | tr '[:upper:]' '[:lower:]' | tr -cd '[:alnum:]_-')_genesis}"
if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
  echo "ОШИБКА: docker-сеть '${NETWORK}' не найдена. Стек поднят?" >&2
  echo "Подскажите имя явно: GENESIS_NETWORK=<сеть> $0" >&2
  echo "Список: docker network ls --filter name=genesis" >&2
  exit 1
fi

# Миграции идут под POSTGRES_USER (владелец схемы), а НЕ под ролью приложения:
# у той намеренно нет прав на DDL.
CONNECTION="Host=postgres;Port=5432;Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD}"

echo "[$(date -u +%FT%TZ)] Накатываю миграции${TARGET:+ до ${TARGET}} на ${PG_DB}..."

# bin/obj каждого проекта — анонимные volume поверх примонтированного исходника.
# Иначе контейнер подхватит obj/*.nuget.g.props, сгенерированные сборкой на хосте:
# там абсолютные пути к NuGet-кэшу хоста, и restore внутри падает с NETSDK1064
# (та же причина, по которой в docker-compose.dev.yml bin/obj вынесены в volumes).
docker run --rm \
  --network "$NETWORK" \
  -v "$COMPOSE_DIR:/src" \
  -v /src/src/GenesisMarket.Domain/bin \
  -v /src/src/GenesisMarket.Domain/obj \
  -v /src/src/GenesisMarket.Infrastructure/bin \
  -v /src/src/GenesisMarket.Infrastructure/obj \
  -v /src/src/GenesisMarket.Api/bin \
  -v /src/src/GenesisMarket.Api/obj \
  -w /src \
  -e GENESIS_DESIGN_CONNECTION="$CONNECTION" \
  "$SDK_IMAGE" \
  sh -c "
    set -e
    export PATH=\"\$PATH:/root/.dotnet/tools\"
    dotnet tool install --global dotnet-ef --version ${EF_VERSION} >/dev/null 2>&1 || true
    dotnet restore src/GenesisMarket.Api/GenesisMarket.Api.csproj
    dotnet ef database update ${TARGET} \
      --project src/GenesisMarket.Infrastructure \
      --startup-project src/GenesisMarket.Api
  "

echo "[$(date -u +%FT%TZ)] Готово."
echo "Если миграция добавила таблицы — выдайте на них права роли приложения:"
echo "  docker exec -i genesis-postgres psql -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" \\"
echo "    -v app_user=\"\$POSTGRES_APP_USER\" -v app_password=\"\$POSTGRES_APP_PASSWORD\" \\"
echo "    -f /sql/create-app-role.sql"
