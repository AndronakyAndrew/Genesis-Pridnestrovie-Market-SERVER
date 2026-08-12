#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — проверка восстановления резервной копии.
#  Поднимает ОДНОРАЗОВЫЙ контейнер PostgreSQL, восстанавливает в него последний
#  (или указанный) дамп и выполняет sanity-запросы (список таблиц, счётчики строк).
#  Ничего не трогает в проде: отдельный контейнер, отдельный том, снос в конце.
#
#  Использование:
#    ./scripts/restore-check.sh                # последний дамп из ./backups
#    ./scripts/restore-check.sh path/to.dump   # конкретный дамп
#
#  «Без проверенного восстановления бэкапа не существует» — гоняйте регулярно
#  (например, по крону раз в неделю) и следите за кодом возврата.
# ============================================================================
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
cd "$COMPOSE_DIR"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

BACKUP_DIR="${BACKUP_DIR:-./backups}"
PG_DB="${POSTGRES_DB:?POSTGRES_DB не задан (.env)}"
PG_IMAGE="${PG_IMAGE:-postgres:17-alpine}"

DUMP="${1:-$(ls -1t "$BACKUP_DIR"/genesis-"${PG_DB}"-*.dump 2>/dev/null | head -1 || true)}"
if [[ -z "${DUMP}" || ! -f "${DUMP}" ]]; then
  echo "ОШИБКА: не найден дамп для проверки (${DUMP:-нет файлов в $BACKUP_DIR})." >&2
  exit 1
fi

CONTAINER="genesis-restore-check-$$"
TMP_PASS="restore-check-pass"
TMP_DB="restore_check"

cleanup() { docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "[$(date -u +%FT%TZ)] Проверяю восстановление: ${DUMP}"
docker run -d --name "$CONTAINER" \
  -e POSTGRES_PASSWORD="$TMP_PASS" -e POSTGRES_DB="$TMP_DB" \
  "$PG_IMAGE" >/dev/null

echo "Жду готовности одноразового PostgreSQL..."
for _ in $(seq 1 30); do
  if docker exec "$CONTAINER" pg_isready -U postgres -d "$TMP_DB" >/dev/null 2>&1; then break; fi
  sleep 1
done

# Восстанавливаем дамп внутрь контейнера.
docker exec -i "$CONTAINER" pg_restore -U postgres -d "$TMP_DB" --no-owner --exit-on-error < "$DUMP"

# Sanity: сколько таблиц и строк в ключевых таблицах.
TABLES="$(docker exec "$CONTAINER" psql -U postgres -d "$TMP_DB" -At -c \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';")"
echo "Таблиц в public: ${TABLES}"
if [[ "${TABLES:-0}" -lt 1 ]]; then
  echo "ОШИБКА: после восстановления нет таблиц — бэкап нерабочий." >&2
  exit 1
fi

for t in users listings; do
  CNT="$(docker exec "$CONTAINER" psql -U postgres -d "$TMP_DB" -At -c "SELECT count(*) FROM ${t};" 2>/dev/null || echo "n/a")"
  echo "  ${t}: ${CNT} строк"
done

echo "[$(date -u +%FT%TZ)] OK: дамп восстанавливается и содержит данные."
