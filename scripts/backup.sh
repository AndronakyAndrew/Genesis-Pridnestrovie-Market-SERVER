#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — резервная копия PostgreSQL.
#  Снимает дамп из работающего контейнера (docker compose exec) в формате
#  custom (-Fc, уже сжат), ротирует старые копии. Запускать из каталога SERVER
#  (где лежит docker-compose.yml) или указать его через переменную COMPOSE_DIR.
#
#  Планирование (crontab, каждый день в 03:30):
#    30 3 * * * cd /opt/genesis/SERVER && ./scripts/backup.sh >> /var/log/genesis-backup.log 2>&1
#
#  Восстановление проверяется отдельным скриптом restore-check.sh.
#  Без проверенного восстановления бэкапа не существует.
# ============================================================================
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
cd "$COMPOSE_DIR"

# Значения берём из .env рядом с docker-compose.yml.
if [[ -f .env ]]; then
  set -a; # экспортируем всё, что объявлено в .env
  # shellcheck disable=SC1091
  source .env
  set +a
fi

BACKUP_DIR="${BACKUP_DIR:-./backups}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
PG_USER="${POSTGRES_USER:?POSTGRES_USER не задан (.env)}"
PG_DB="${POSTGRES_DB:?POSTGRES_DB не задан (.env)}"

mkdir -p "$BACKUP_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$BACKUP_DIR/genesis-${PG_DB}-${STAMP}.dump"

echo "[$(date -u +%FT%TZ)] Снимаю дамп ${PG_DB} -> ${OUT}"
# -T: без псевдо-tty; -Fc: custom-формат (сжат, пригоден для pg_restore -j).
docker compose exec -T postgres \
  pg_dump -U "$PG_USER" -Fc "$PG_DB" > "$OUT"

SIZE="$(wc -c < "$OUT")"
if [[ "$SIZE" -lt 1024 ]]; then
  echo "ОШИБКА: дамп подозрительно мал (${SIZE} байт) — прерываю." >&2
  exit 1
fi
echo "[$(date -u +%FT%TZ)] Готово: ${OUT} (${SIZE} байт)"

# Ротация: удаляем дампы старше RETENTION_DAYS суток.
find "$BACKUP_DIR" -name "genesis-${PG_DB}-*.dump" -type f -mtime "+${RETENTION_DAYS}" -print -delete \
  | sed 's/^/[ротация] удалён /' || true

echo "[$(date -u +%FT%TZ)] Актуальные копии:"
ls -1t "$BACKUP_DIR"/genesis-"${PG_DB}"-*.dump 2>/dev/null | head -5
