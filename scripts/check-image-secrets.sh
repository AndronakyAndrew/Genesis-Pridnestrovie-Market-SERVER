#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — проверка образа API на утечку секретов в слои.
#  Секреты приходят ТОЛЬКО через runtime-env (docker-compose), в образ не пишутся.
#  Скрипт это подтверждает: (1) в истории слоёв нет секретоподобных значений,
#  (2) в published-файлах appsettings у секретных ключей пусто.
#
#  Использование:
#    ./scripts/check-image-secrets.sh                 # соберёт образ genesis-api:scan
#    IMAGE=myrepo/genesis-api:tag ./scripts/check-image-secrets.sh   # готовый образ
# ============================================================================
set -euo pipefail

COMPOSE_DIR="${COMPOSE_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
cd "$COMPOSE_DIR"

IMAGE="${IMAGE:-genesis-api:scan}"
if [[ "$IMAGE" == "genesis-api:scan" ]]; then
  echo "[*] Собираю образ ${IMAGE}..."
  docker build -t "$IMAGE" -f Dockerfile . >/dev/null
fi

FAIL=0

echo "[*] Ищу секретоподобные строки в истории слоёв (docker history)..."
# Шаблоны имён секретов + build-arg присвоений. В корректном образе совпадений нет.
if docker history --no-trunc --format '{{.CreatedBy}}' "$IMAGE" \
    | grep -Ei 'JWT_SECRET|IPHASH_KEY|POSTGRES_PASSWORD|MINIO_ROOT_PASSWORD|SMTP_PASSWORD|TELEGRAM_BOT_TOKEN|Jwt__Key|Security__IpHashKey|password=|secret=' ; then
  echo "ОШИБКА: в истории слоёв найдены секретоподобные значения (см. выше)." >&2
  FAIL=1
else
  echo "    OK: в истории слоёв секретов нет."
fi

echo "[*] Проверяю, что в published appsettings секреты пустые..."
# У секретных ключей в закоммиченном appsettings.json значения пустые (секреты — из env).
LEAK="$(docker run --rm --entrypoint sh "$IMAGE" -c \
  'grep -Eo "\"(Key|Password|SecretKey|BotToken|IpHashKey)\"[[:space:]]*:[[:space:]]*\"[^\"]+\"" appsettings.json 2>/dev/null || true')"
if [[ -n "$LEAK" ]]; then
  echo "ОШИБКА: в appsettings.json образа есть непустые секреты:" >&2
  echo "$LEAK" >&2
  FAIL=1
else
  echo "    OK: секретные ключи в appsettings.json пусты."
fi

if [[ "$FAIL" -eq 0 ]]; then
  echo "[$(date -u +%FT%TZ)] Готово: секретов в образе не обнаружено."
else
  echo "[$(date -u +%FT%TZ)] ПРОВАЛ: образ содержит секреты — не публиковать." >&2
fi
exit "$FAIL"
