# Публичный API через Cloudflare Tunnel

Заменил ngrok 2026-09-07. Домен свой (`genesis-hq.com`), адрес API постоянный:

| Что | Адрес |
|---|---|
| API (этот проект) | `https://api.genesis-hq.com` |
| Фронтенд (Vercel) | `https://market.genesis-hq.com` |

## Как это устроено

```
браузер ──TLS──> Cloudflare edge ──туннель──> cloudflared на ноутбуке
                                                     │ http://127.0.0.1:8090
                                                     ▼
                                          docker: genesis-api (Kestrel :8080)
```

Входящих портов на ноутбуке нет вообще: `cloudflared` держит исходящее
соединение к Cloudflare. API по-прежнему опубликован только на
`127.0.0.1:8090` (`docker-compose.yml`), PostgreSQL и MinIO портов на хосте
не имеют. Обойти туннель снаружи нельзя — снаружи просто нечего слушать.

TLS-сертификат выпускает и продлевает Cloudflare. Caddy-overlay
(`docker-compose.proxy.yml`) — **альтернатива** этой схеме, а не дополнение:
он нужен, только если сервер получит белый IP. Одновременно не поднимаем.

## Настройка туннеля

Разово, на серверном ноутбуке:

```bash
cloudflared tunnel login                 # выбрать зону genesis-hq.com
cloudflared tunnel create genesis        # создаёт креды ~/.cloudflared/<UUID>.json
cloudflared tunnel route dns genesis api.genesis-hq.com
```

`~/.cloudflared/config.yml`:

```yaml
tunnel: genesis
credentials-file: /home/<user>/.cloudflared/<UUID>.json

ingress:
  - hostname: api.genesis-hq.com
    service: http://127.0.0.1:8090
    originRequest:
      connectTimeout: 5s
      # Kestrel держит keep-alive 120s (KESTREL_KEEPALIVE_SECONDS).
      keepAliveTimeout: 90s
  # Обязательное правило-заглушка: без него cloudflared не стартует.
  - service: http_status:404
```

Автозапуск (иначе после перезагрузки ноутбука API пропадает из интернета):

```bash
sudo cloudflared service install
sudo systemctl enable --now cloudflared
```

Windows: `cloudflared service install` из консоли администратора — регистрирует
службу `Cloudflared`, стартует автоматически.

Файл кредов туннеля (`<UUID>.json`) — это секрет уровня «кто угодно может
опубликоваться на нашем домене». В репозиторий не кладём никогда.

### Настройки в панели Cloudflare

- **SSL/TLS mode: Full (strict)** — иначе edge может ходить в origin по
  открытому HTTP из интернета; с туннелем это не нужно и не должно быть можно.
- **Always Use HTTPS: on**, **Min TLS Version: 1.2**.
- Прокси-облачко на DNS-записи `api` — оранжевое (иначе туннель не работает).
- Кеш для `api.genesis-hq.com` не включаем: ответы API персональные.

## Что должно быть в `.env`

```dotenv
ASPNETCORE_ENVIRONMENT=Production
CORS_ALLOWED_ORIGINS=https://market.genesis-hq.com
SEO_WEB_BASE_URL=https://market.genesis-hq.com
TELEGRAM_WEB_BASE_URL=https://market.genesis-hq.com
API_HOST_PORT=8090
TRUSTED_PROXY_NETWORKS=172.16.0.0/12
```

`CORS_ALLOWED_ORIGINS` — только боевой origin. `*` нельзя, `http://localhost:*`
в проде нельзя (см. журнал безопасности, находка №28).

## Реальный IP клиента и rate-limit

API ограничивает запросы **по реальному IP**: 120 запросов в минуту на весь
API и 60 в минуту на каталог/поиск; регистрация, вход, публикация, жалобы и
раскрытие контактов — отдельные, более строгие лимиты. При превышении —
`429 Too Many Requests` и `Retry-After`.

Реальный IP берётся из `X-Forwarded-For`, который ставит edge Cloudflare
(он же дублирует его в `CF-Connecting-IP`). Приложение доверяет этому
заголовку только от адреса из `TRUSTED_PROXY_NETWORKS=172.16.0.0/12` —
docker-моста, через который запрос с хост-порта попадает в контейнер.
Не отключайте эту переменную: без неё все посетители считаются одним IP,
и глобальный лимит 120/мин уронит сайт целиком для всех сразу.

**Проверять обязательно после переезда** — лимит по одному IP не должен
задевать соседей:

```bash
# 1) Пришёл ли реальный IP: 130 быстрых запросов снаружи должны дать 429.
for i in $(seq 1 130); do
  curl -s -o /dev/null -w '%{http_code} ' https://api.genesis-hq.com/api/stats
done; echo

# 2) ...а через loopback (мимо туннеля) счётчик должен быть свой.
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8090/api/stats
```

Если снаружи 429 не наступает вообще или, наоборот, наступает после
десятка запросов с разных устройств — XFF разбирается неверно, смотри
`Network__KnownNetworks` и `Network__ForwardLimit` в `docker-compose.yml`.

Значения лимитов меняются в `.env` без пересборки образа:

```dotenv
RATE_LIMIT_GLOBAL_PER_MINUTE=120
RATE_LIMIT_SEARCH_PER_MINUTE=60
RATE_LIMIT_SENSITIVE_ANON_PER_HOUR=3
RATE_LIMIT_CREATE_LISTING_PER_HOUR=10
```

После изменения: `I_CONFIRM_PROD_LAUNCH=yes docker compose up -d api`.

Для распределённой атаки с множества IP этих лимитов недостаточно — но теперь
перед origin стоит Cloudflare: WAF, Bot Fight Mode и «Under Attack» включаются
в панели, кода не трогают. CAPTCHA на чтение каталога не ставим; только на
регистрацию, вход или создание объявления, и только если начнётся накрутка.

## Запуск и проверка

```bash
# 1. API
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --build api

# 2. Локально — стек здоров, порты закрыты, заголовки на месте
./scripts/smoke-deploy.sh

# 3. Снаружи — тот же smoke через туннель (проверяет ещё и HSTS)
BASE_URL=https://api.genesis-hq.com ./scripts/smoke-deploy.sh

# 4. Туннель жив
cloudflared tunnel info genesis
systemctl status cloudflared      # либо журнал службы на Windows
```

`smoke-deploy.sh` снаружи проверяет `[4] Порты инфраструктуры` относительно
`127.0.0.1`; чтобы убедиться, что через туннель не торчит лишний hostname,
достаточно посмотреть `ingress` в `config.yml` — правил ровно два.

## Если API пропал из интернета

1. `systemctl status cloudflared` — служба упала или не поднялась после
   перезагрузки (самая частая причина).
2. `curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8090/health/live`
   — если 200, проблема в туннеле, а не в API.
3. `cloudflared tunnel info genesis` — есть ли активные подключения к edge.
4. Панель Cloudflare → DNS: запись `api` на месте, облачко оранжевое.
