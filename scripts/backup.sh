#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — резервная копия PostgreSQL и объектов MinIO.
#
#  PostgreSQL: pg_dump в формате custom (-Fc, уже сжат).
#  MinIO: tar.gz каталога /data (фото объявлений и аватары).
#  Обе копии ротируются по BACKUP_RETENTION_DAYS.
#
#  Обращаемся к контейнерам напрямую через `docker exec` по container_name,
#  а НЕ через `docker compose exec`: Compose интерполирует docker-compose.yml
#  на любой команде, а там стоит guard ${I_CONFIRM_PROD_LAUNCH:?...} — из cron
#  переменной нет, и бэкап падал бы каждую ночь. Прямой docker exec от guard
#  не зависит.
#
#  Планирование (crontab, каждый день в 03:30):
#    30 3 * * * cd /opt/genesis/SERVER && ./scripts/backup.sh >> /var/log/genesis-backup.log 2>&1
#
#  ВАЖНО, что этот скрипт НЕ делает — сделай руками:
#    * копии лежат на той же машине. Настрой выгрузку BACKUP_DIR на внешнее
#      хранилище (rclone/rsync/S3) — при потере сервера иначе теряется всё;
#    * копии не шифруются. Если хранилище чужое — заверни в age/gpg.
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
PG_CONTAINER="${PG_CONTAINER:-genesis-postgres}"
MINIO_CONTAINER="${MINIO_CONTAINER:-genesis-minio}"

mkdir -p "$BACKUP_DIR"
# Дампы содержат персональные данные — каталог не должен читаться кем попало.
chmod 700 "$BACKUP_DIR"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$BACKUP_DIR/genesis-${PG_DB}-${STAMP}.dump"

require_running() {
  local name="$1"
  if ! docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null | grep -q true; then
    echo "ОШИБКА: контейнер ${name} не запущен — бэкап невозможен." >&2
    exit 1
  fi
}

# ---- PostgreSQL ----
require_running "$PG_CONTAINER"
echo "[$(date -u +%FT%TZ)] Снимаю дамп ${PG_DB} -> ${OUT}"
# -i: stdin не нужен, но нужен неинтерактивный режим; -Fc: custom (сжат, pg_restore -j).
docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -Fc "$PG_DB" > "$OUT"
chmod 600 "$OUT"

SIZE="$(wc -c < "$OUT")"
if [[ "$SIZE" -lt 1024 ]]; then
  echo "ОШИБКА: дамп подозрительно мал (${SIZE} байт) — прерываю." >&2
  exit 1
fi
echo "[$(date -u +%FT%TZ)] Готово: ${OUT} (${SIZE} байт)"

# ---- MinIO ----
# Фото объявлений и аватары. Без них восстановленная база показывает битые картинки.
MINIO_OUT="$BACKUP_DIR/genesis-minio-${STAMP}.tar.gz"
require_running "$MINIO_CONTAINER"
echo "[$(date -u +%FT%TZ)] Архивирую объекты MinIO -> ${MINIO_OUT}"
docker exec "$MINIO_CONTAINER" tar -czf - -C /data . > "$MINIO_OUT"
chmod 600 "$MINIO_OUT"

MINIO_SIZE="$(wc -c < "$MINIO_OUT")"
if [[ "$MINIO_SIZE" -lt 100 ]]; then
  echo "ОШИБКА: архив MinIO подозрительно мал (${MINIO_SIZE} байт) — прерываю." >&2
  exit 1
fi
echo "[$(date -u +%FT%TZ)] Готово: ${MINIO_OUT} (${MINIO_SIZE} байт)"

# ---- Ротация: удаляем копии старше RETENTION_DAYS суток ----
find "$BACKUP_DIR" \( -name "genesis-${PG_DB}-*.dump" -o -name "genesis-minio-*.tar.gz" \) \
  -type f -mtime "+${RETENTION_DAYS}" -print -delete \
  | sed 's/^/[ротация] удалён /' || true

echo "[$(date -u +%FT%TZ)] Актуальные копии:"
ls -1t "$BACKUP_DIR"/genesis-* 2>/dev/null | head -10
