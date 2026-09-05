#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — smoke-тест развёрнутого стека.
#
#  Проверяет РЕАЛЬНО ПОДНЯТЫЙ стек, а не конфигурацию: ходит по HTTP и по
#  портам так же, как это сделал бы посторонний. Конфигурацию не мокает.
#
#  Запускать после каждого деплоя (и в CI перед выкладкой):
#    ./scripts/smoke-deploy.sh                       # против http://127.0.0.1:8090
#    BASE_URL=https://market.example.com ./scripts/smoke-deploy.sh
#
#  Код возврата 0 — все проверки прошли; 1 — есть провалы (деплой не принимать).
# ============================================================================
set -uo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:${API_HOST_PORT:-8090}}"
# Адрес, по которому стек виден «снаружи». Для локальной проверки берём тот же
# хост, но порты БД/MinIO должны быть закрыты на ЛЮБОМ внешнем интерфейсе.
EXTERNAL_HOST="${EXTERNAL_HOST:-127.0.0.1}"

PASS=0
FAIL=0

ok()   { echo "  [ OK ] $1"; PASS=$((PASS + 1)); }
bad()  { echo "  [FAIL] $1" >&2; FAIL=$((FAIL + 1)); }

status_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$1"; }
headers_of() { curl -s -D - -o /dev/null --max-time 10 "$1"; }

echo "== Smoke-тест: ${BASE_URL} =="

# ---- 1. Приложение вообще живо ----
echo "[1] Живость"
if [[ "$(status_of "${BASE_URL}/health/live")" == "200" ]]; then
  ok "/health/live отвечает 200"
else
  bad "/health/live не отвечает 200 — дальше проверять нечего"
  exit 1
fi

if [[ "$(status_of "${BASE_URL}/health/ready")" == "200" ]]; then
  ok "/health/ready отвечает 200 (зависимости доступны)"
else
  bad "/health/ready не 200: PostgreSQL или MinIO недоступны"
fi

# /health/ready не должен раскрывать состав стека.
READY_BODY="$(curl -s --max-time 10 "${BASE_URL}/health/ready")"
if grep -qiE 'postgres|minio|npgsql|exception|stacktrace' <<<"$READY_BODY"; then
  bad "/health/ready раскрывает детали зависимостей: ${READY_BODY:0:120}"
else
  ok "/health/ready не раскрывает состав стека"
fi

# ---- 2. Swagger закрыт ----
echo "[2] Swagger"
for path in /swagger /swagger/index.html /swagger/v1/swagger.json; do
  code="$(status_of "${BASE_URL}${path}")"
  if [[ "$code" == "404" || "$code" == "401" ]]; then
    ok "${path} -> ${code}"
  else
    bad "${path} -> ${code} (ожидался 404/401; в Production Swagger не поднимается)"
  fi
done

# ---- 3. Заголовки безопасности ----
echo "[3] Заголовки безопасности"
HEADERS="$(headers_of "${BASE_URL}/health/live")"
check_header() {
  local name="$1" expected="$2"
  local line
  line="$(grep -i "^${name}:" <<<"$HEADERS" | tr -d '\r')"
  if [[ -z "$line" ]]; then
    bad "нет заголовка ${name}"
  elif [[ -n "$expected" ]] && ! grep -qi "$expected" <<<"$line"; then
    bad "${name}: ожидалось '${expected}', получено '${line}'"
  else
    ok "${line}"
  fi
}
check_header "X-Content-Type-Options" "nosniff"
check_header "X-Frame-Options"        "DENY"
check_header "Referrer-Policy"        "no-referrer"
check_header "Content-Security-Policy" "default-src 'none'"
check_header "Permissions-Policy"     "geolocation=()"

# Версия сервера наружу не уходит.
for leaky in "Server" "X-Powered-By" "X-AspNet-Version"; do
  if grep -qi "^${leaky}:" <<<"$HEADERS"; then
    bad "отдаётся заголовок ${leaky}: $(grep -i "^${leaky}:" <<<"$HEADERS" | tr -d '\r')"
  else
    ok "заголовка ${leaky} нет"
  fi
done

# HSTS обязателен, если проверяем по HTTPS (за TLS-терминатором).
if [[ "$BASE_URL" == https://* ]]; then
  check_header "Strict-Transport-Security" "max-age="
fi

# ---- 4. Порты инфраструктуры закрыты снаружи ----
echo "[4] Порты инфраструктуры"
port_closed() {
  local host="$1" port="$2" name="$3"
  if timeout 3 bash -c "cat < /dev/null > /dev/tcp/${host}/${port}" 2>/dev/null; then
    bad "${name} отвечает на ${host}:${port} — порт должен быть закрыт"
  else
    ok "${name} (${host}:${port}) недоступен"
  fi
}
port_closed "$EXTERNAL_HOST" 5434 "PostgreSQL"
port_closed "$EXTERNAL_HOST" 9000 "MinIO API"
port_closed "$EXTERNAL_HOST" 9001 "MinIO Console"
port_closed "$EXTERNAL_HOST" 8081 "Adminer"

# ---- 5. CORS не открыт настежь ----
echo "[5] CORS"
CORS="$(curl -s -D - -o /dev/null --max-time 10 \
  -H "Origin: https://evil.example" \
  -H "Access-Control-Request-Method: GET" \
  -X OPTIONS "${BASE_URL}/health/live" | tr -d '\r')"
if grep -qi "access-control-allow-origin: \*" <<<"$CORS"; then
  bad "CORS отдаёт Access-Control-Allow-Origin: * "
elif grep -qi "access-control-allow-origin: https://evil.example" <<<"$CORS"; then
  bad "CORS принимает произвольный origin (https://evil.example)"
else
  ok "чужой origin не попадает в Access-Control-Allow-Origin"
fi

# ---- Итог ----
echo
echo "== Пройдено: ${PASS}, провалено: ${FAIL} =="
[[ "$FAIL" -eq 0 ]] || { echo "Деплой не принимать." >&2; exit 1; }
echo "Стек прошёл smoke-тест."
