# Выкат Telegram-интеграции на прод

Что выкатывается (коммит «feat(telegram): публикация объявлений в канал и обратная связь»,
влит в `master` мержем «Слияние feature/telegram-channel в master»):

- служебный бот: обращения пользователей пересылаются админу, ответ реплаем уходит обратно;
- очередь публикации одобренных объявлений в канал (`channel_post_queue`) и её воркер;
- редирект `GET /r/l/{id}?s=tg` с учётом переходов (`link_clicks`);
- колонка `listings."TelegramPublishedAt"`;
- пакет `tzdata` в образе API (часовой пояс окна публикации).

**Публикация в канал после выката выключена** (`TELEGRAM_PUBLISHING_ENABLED=false`).
Очередь при этом наполняется. Включение — отдельный шаг 10, после проверки.

Всё выполняется на сервере по SSH, в bash.

---

## Что нужно знать о проекте перед началом

| Что | Значение | Откуда |
|---|---|---|
| Каталог стека на сервере | `/home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER` (корень git-репозитория) | метка `com.docker.compose.project.working_dir` контейнера `genesis-api` |
| Compose-файл прода | `docker-compose.yml` в этом каталоге | |
| Сервис API в compose / контейнер | `api` / `genesis-api` | `docker-compose.yml` |
| Контейнер PostgreSQL | `genesis-postgres` | `docker-compose.yml` |
| База / владелец схемы | `genesis` / `genesis` (`POSTGRES_DB`, `POSTGRES_USER` в `.env`) | `.env` |
| Порт API на хосте | `127.0.0.1:8090` (наружу — Cloudflare Tunnel, `https://api.genesis-hq.com`) | `API_HOST_PORT`, `docs/cloudflare-tunnel.md` |
| Имя compose-проекта | `genesis-pridnestrovie-market-server` (имя каталога в нижнем регистре) | сеть `genesis-pridnestrovie-market-server_genesis`, образ `genesis-pridnestrovie-market-server-api` |

`README.md` и шапка `scripts/backup.sh` упоминают каталог `/opt/genesis/SERVER` — на этом сервере его
нет. Скрипты от этого не зависят: они работают из каталога, где лежат.

**Как в этом проекте применяются миграции.** Приложение миграции при старте **не** накатывает
(у его роли нет прав на DDL). Схему меняет отдельный шаг — `./scripts/migrate.sh`: он поднимает
одноразовый контейнер `mcr.microsoft.com/dotnet/sdk:10.0-alpine` в сети стека, ставит `dotnet-ef 10.0.0`
и выполняет `dotnet ef database update` под `POSTGRES_USER` (владелец схемы). Скрипту нужен интернет
(образ SDK и NuGet) и 2–5 минут. Если код ушёл вперёд схемы, `/health/ready` отвечает не 200, а в логе
API пишется `Схема базы данных отстаёт от кода ... Выполните шаг деплоя scripts/migrate.sh.`

**Guard прода.** Любая команда `docker compose` с `docker-compose.yml` требует
`I_CONFIRM_PROD_LAUNCH=yes` прямо в команде (не в `.env`). Без неё команда падает с текстом
«ЭТО ПРОД…» — это нормально, повторите с переменной. Скрипты `backup.sh` и `migrate.sh` compose
не вызывают, им переменная не нужна.

**Миграция этого выката:** `20260915070852_AddTelegramChannelPublishing`
(файл `src/GenesisMarket.Infrastructure/Persistence/Migrations/20260915070852_AddTelegramChannelPublishing.cs`).
Предыдущая, до которой откатываться: `20260913142037_AddListingRejectionReason`.

---

## 0. Подготовка: запомнить текущее состояние для отката

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
git remote -v            # ожидается github.com/AndronakyAndrew/Genesis-Pridnestrovie-Market-SERVER
git status --short       # ожидается только: ?? docker-compose.admin.yml

# Текущий коммит и образ API — понадобятся при откате.
# При этом выкате master уже выровнен на origin/master (см. шаг 3), поэтому коммит
# «до выката» берём из резервной ветки, а не из HEAD.
git rev-parse server-master-backup-20260915 | tee ~/genesis-pre-telegram.commit
docker inspect -f '{{.Config.Image}}' genesis-api | tee ~/genesis-pre-telegram.image
docker tag "$(cat ~/genesis-pre-telegram.image)" genesis-pridnestrovie-market-server-api:pre-telegram
docker image ls genesis-pridnestrovie-market-server-api
```

Если `git status --short` показывает что-то кроме `?? docker-compose.admin.yml` — остановитесь и
разберитесь, что изменено на сервере руками.

---

## 1. Бэкап БД (и MinIO)

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
./scripts/backup.sh
ls -lt backups/ | head -5
```

Ожидается: `Готово: ./backups/genesis-genesis-<время>.dump (… байт)` и архив MinIO.
Скрипт сам прерывается, если дамп меньше 1 КБ или архив MinIO не читается как gzip.
Запомните имя свежего дампа — оно выведено последней строкой `ls`.

**Внимание:** на сервере в `crontab` нет задач бэкапа (проверено 2026-09-15), хотя `README.md` их
описывает. Ночных копий нет — дамп этого шага может оказаться единственным. Этот выкат расписание
не настраивает.

---

## 2. Обновление `.env`

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
cp .env ".env.bak.$(date -u +%Y%m%dT%H%M%SZ)"

# Новые переменные этого выката — дописать, если их ещё нет.
grep -q '^TELEGRAM_PUBLISHING_ENABLED=' .env    || echo 'TELEGRAM_PUBLISHING_ENABLED=false' >> .env
grep -q '^TELEGRAM_ENABLE_UPDATE_POLLING=' .env || echo 'TELEGRAM_ENABLE_UPDATE_POLLING=true' >> .env

# Проверка заполненности (значения не печатаются).
for k in TELEGRAM_BOT_TOKEN TELEGRAM_ADMIN_CHAT_ID TELEGRAM_CHANNEL_ID \
         TELEGRAM_ENABLE_UPDATE_POLLING TELEGRAM_PUBLISHING_ENABLED; do
  if grep -q "^$k=." .env; then echo "$k: задано"; else echo "$k: ПУСТО"; fi
done
grep '^TELEGRAM_PUBLISHING_ENABLED=' .env      # на этом шаге должно быть =false
```

Если `TELEGRAM_ADMIN_CHAT_ID` или `TELEGRAM_CHANNEL_ID` пустые — впишите их руками (`nano .env`):
`TELEGRAM_ADMIN_CHAT_ID` — числовой личный chat_id админа, `TELEGRAM_CHANNEL_ID` — `@имя` канала или
его числовой id `-100…`. Пустые значения API не роняют: бот и публикация просто не запустятся
(см. раздел проверки).

`TELEGRAM_ENABLE_UPDATE_POLLING=true` допустимо, только если этот же токен бота больше **нигде** не
опрашивает Telegram (ни локально у разработчика, ни на другом сервере) — иначе бот получает 409.

### Полный список переменных `.env`

Статус: **новая** — появилась в этом выкате; **есть** — была и раньше, не менялась;
**смысл изменился** — была, но теперь на публикацию в канал не влияет.

| Переменная | Статус | Назначение |
|---|---|---|
| `TELEGRAM_PUBLISHING_ENABLED` | **новая** | `false` — воркер публикации в канал не запускается. Включение — шаг 10 |
| `TELEGRAM_ENABLE_UPDATE_POLLING` | **новая** | long polling служебного бота; не задано → `true` |
| `TELEGRAM_ADMIN_CHAT_ID` | **новая** (в compose) | chat_id админа для обращений; не задано → бот не запускается |
| `TELEGRAM_CHANNEL_ID` | **новая** (в compose) | канал публикации; не задано → публикация не запускается |
| `TELEGRAM_BOT_TOKEN` | есть | токен бота (секрет) — общий для бота, публикации и уведомлений |
| `TELEGRAM_BROADCAST_CHAT_ID` | смысл изменился | больше не используется для постов в канал; можно оставить |
| `TELEGRAM_WEB_BASE_URL` | смысл изменился | больше не используется для постов в канал; можно оставить |
| `ASPNETCORE_ENVIRONMENT` | есть | `Production` |
| `ASPNETCORE_HTTP_PORTS` | есть | порт Kestrel в контейнере (8080) |
| `LOG_LEVEL` | есть | уровень Serilog |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | есть | БД и владелец схемы |
| `POSTGRES_APP_USER`, `POSTGRES_APP_PASSWORD` | есть | ограниченная роль приложения (если заведена) |
| `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_HOST_PORT` | есть | для внешних инструментов, API их не читает |
| `MINIO_ENDPOINT`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `MINIO_BUCKET`, `MINIO_USE_SSL` | есть | хранилище фото |
| `JWT_SECRET`, `JWT_ISSUER`, `JWT_AUDIENCE`, `JWT_EXPIRES_DAYS` | есть | JWT |
| `IPHASH_KEY` | есть | HMAC для IP (используется и для `link_clicks`) |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM`, `SMTP_FROM_NAME`, `SMTP_USE_SSL` | есть | письма |
| `RESEND_API_KEY`, `RESEND_FROM_EMAIL`, `FEEDBACK_NOTIFICATION_EMAIL` | есть | обратная связь |
| `SEO_WEB_BASE_URL`, `SEO_SITE_NAME`, `SITE_BASE_URL` | есть | SEO |
| `PUBLISHING_REQUIRED_VERIFICATION` | есть | условия публикации объявлений на площадке |
| `CORS_ALLOWED_ORIGINS`, `CORS_PREFLIGHT_MAX_AGE_SECONDS` | есть | CORS |
| `TRUSTED_PROXY_NETWORKS`, `TRUSTED_PROXY_IPS`, `TRUSTED_PROXY_FORWARD_LIMIT` | есть | доверенные прокси |
| `RATE_LIMIT_*` (6 шт.) | есть | лимиты |
| `KESTREL_*` (4 шт.) | есть | лимиты Kestrel |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | есть | OpenTelemetry |
| `API_HOST_PORT` | есть | `8090` |
| `BACKUP_RETENTION_DAYS` | есть | `backup.sh` |
| `PUBLIC_DOMAIN`, `ACME_EMAIL` | есть | только для Caddy-overlay, не используется |

Интервал, окно и прочие параметры публикации **не** задаются через `.env`: они в
`src/GenesisMarket.Api/appsettings.json`, секция `ChannelPublishing`
(не чаще раза в 20 мин, окно 09:00–21:00 `Europe/Chisinau`, 3 попытки, описание до 120 символов,
фото и кнопка — от `https://api.genesis-hq.com`). Переопределить можно только добавив
`ChannelPublishing__…` в `environment` сервиса `api` в `docker-compose.yml`.

---

## 3. Получение кода

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
git fetch origin
git checkout master
git pull --ff-only origin master
git log --oneline -3
ls src/GenesisMarket.Infrastructure/Persistence/Migrations/20260915070852_AddTelegramChannelPublishing.cs
```

Ожидается: вверху `git log` — «Слияние feature/telegram-channel в master», файл миграции существует.
Если `--ff-only` отказывается — на сервере есть коммиты, которых нет в GitHub: остановитесь.

При этом выкате так и было: серверный `master` содержал merge-коммиты прошлых `git pull` и
`ffbe51a «Commiting past changes on the server»`, но по содержимому совпадал с GitHub
(`git diff --quiet origin/master~1 master`). Прежнее состояние сохранено веткой
`server-master-backup-20260915`, `master` выровнен командой `git reset --hard origin/master`.
Следующие выкаты `--ff-only` пройдут, если на сервере снова не коммитить.

---

## 4. Сборка образа (контейнер пока не перезапускается)

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
I_CONFIRM_PROD_LAUNCH=yes docker compose build api
```

Ожидается успешная сборка; в выводе есть шаг `RUN apk add --no-cache tzdata`.
Работающий контейнер `genesis-api` продолжает обслуживать запросы старым кодом.

---

## 5. Просмотр SQL миграции перед применением

Команда собирает проект в одноразовом SDK-контейнере (как `migrate.sh`) и только печатает SQL,
к БД не подключается:

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
docker run --rm \
  -v /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER:/src \
  -v /src/src/GenesisMarket.Domain/bin -v /src/src/GenesisMarket.Domain/obj \
  -v /src/src/GenesisMarket.Infrastructure/bin -v /src/src/GenesisMarket.Infrastructure/obj \
  -v /src/src/GenesisMarket.Api/bin -v /src/src/GenesisMarket.Api/obj \
  -v /tmp:/out \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0-alpine \
  sh -c 'export PATH="$PATH:/root/.dotnet/tools"
         dotnet tool install --global dotnet-ef --version 10.0.0 >/dev/null 2>&1 || true
         dotnet ef migrations script \
           20260913142037_AddListingRejectionReason 20260915070852_AddTelegramChannelPublishing \
           --project src/GenesisMarket.Infrastructure --startup-project src/GenesisMarket.Api \
           -o /out/AddTelegramChannelPublishing.sql'
cat /tmp/AddTelegramChannelPublishing.sql
```

Ожидаемое содержимое (сверьте):

```sql
START TRANSACTION;
ALTER TABLE listings ADD "TelegramPublishedAt" timestamp with time zone;

CREATE TABLE channel_post_queue (
    "Id" uuid NOT NULL,
    "ListingId" uuid NOT NULL,
    "Status" character varying(16) NOT NULL DEFAULT 'Pending',
    "EnqueuedAt" timestamp with time zone NOT NULL,
    "PublishedAt" timestamp with time zone,
    "AttemptCount" integer NOT NULL,
    "LastError" character varying(2048),
    CONSTRAINT "PK_channel_post_queue" PRIMARY KEY ("Id"),
    CONSTRAINT ck_channel_post_queue_attempts_nonnegative CHECK ("AttemptCount" >= 0),
    CONSTRAINT "FK_channel_post_queue_listings_ListingId" FOREIGN KEY ("ListingId") REFERENCES listings ("Id") ON DELETE CASCADE
);

CREATE TABLE link_clicks (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ListingId" uuid NOT NULL,
    "Source" character varying(32) NOT NULL,
    "IpHash" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_link_clicks" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_channel_post_queue_ListingId" ON channel_post_queue ("ListingId");
CREATE INDEX "IX_channel_post_queue_Status_EnqueuedAt" ON channel_post_queue ("Status", "EnqueuedAt");
CREATE INDEX "IX_link_clicks_ListingId_CreatedAt" ON link_clicks ("ListingId", "CreatedAt");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260915070852_AddTelegramChannelPublishing', '10.0.0');
COMMIT;
```

`ALTER TABLE listings ADD` — nullable-колонка без значения по умолчанию: таблица не переписывается,
но на мгновение берётся эксклюзивная блокировка `listings`; при долгом запросе к `listings` миграция
его подождёт.

---

## 6. Применение миграции

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
./scripts/migrate.sh
```

Ожидается в конце: `Готово.` Проверка:

```bash
docker exec genesis-postgres psql -U genesis -d genesis -c \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 2;'
docker exec genesis-postgres psql -U genesis -d genesis -c '\d channel_post_queue' -c '\d link_clicks'
```

Первой строкой — `20260915070852_AddTelegramChannelPublishing`.

Старый контейнер API в этот момент ещё работает — это безопасно: старый код новых таблиц не
касается, новая колонка nullable.

Если `migrate.sh` пишет `docker-сеть 'genesis-pridnestrovie-market-server_genesis' не найдена` — стек не поднят или каталог назван
иначе; список сетей: `docker network ls --filter name=genesis`.

---

## 7. Права роли приложения на новые таблицы (только если роль заведена)

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
if grep -q '^POSTGRES_APP_USER=.' .env; then echo "роль задана — выполните команду ниже"; \
else echo "POSTGRES_APP_USER не задан — API ходит под POSTGRES_USER, шаг пропустить"; fi
```

Если роль задана:

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
( source scripts/load-env.sh && load_env .env && \
  docker exec -i genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
    -v app_user="$POSTGRES_APP_USER" -v app_password="$POSTGRES_APP_PASSWORD" \
    -f /sql/create-app-role.sql )
```

Скобки — подоболочка: пароли из `.env` существуют только на время команды и не остаются в вашей
SSH-сессии.

`.env` читается через `scripts/load-env.sh`, а не `source .env`: значения вроде
`SMTP_FROM_NAME=Местная площадка Genesis` shell разбирает как команды.
Скрипт идемпотентен и выдаёт в том числе права на sequence `link_clicks` (identity).

---

## 8. Перезапуск API на новом образе

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --no-build api
until curl -fs -o /dev/null http://127.0.0.1:8090/health/live; do sleep 2; done; echo "API жив"
```

Туннель перезапускать не нужно — `cloudflared` сам переподключится к `127.0.0.1:8090`.

---

## 9. Проверка

### 9.1. Smoke и готовность

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
./scripts/smoke-deploy.sh
BASE_URL=https://api.genesis-hq.com ./scripts/smoke-deploy.sh
curl -s -o /dev/null -w 'ready: %{http_code}\n' http://127.0.0.1:8090/health/ready   # 200
docker exec genesis-api ls /usr/share/zoneinfo/Europe/Chisinau                        # файл есть
```

`ready: 503` при здоровых Postgres и MinIO означает, что миграция не применена (см. лог ниже).

Если `smoke-deploy.sh` проваливает `[4] PostgreSQL отвечает на 127.0.0.1:5434`, проверьте
`docker port genesis-postgres`: порт 5434 публикует неотслеживаемый `docker-compose.admin.yml`
(`127.0.0.1:5434:5432`). К этому выкату провал не относится, но по правилам проекта у PostgreSQL
портов на хосте быть не должно.

### 9.2. Логи

Логи API — JSON Serilog, текст сообщения в поле `@m`.

```bash
docker logs --since 15m genesis-api 2>&1 | grep -E 'Telegram|ChannelPublishing|TELEGRAM|tzdata|migrate\.sh'
```

Должно быть (флаг публикации выключен):

- `Публикация в Telegram-канал выключена (ChannelPublishing:Enabled).`
- бот, если заданы токен и `TELEGRAM_ADMIN_CHAT_ID`: `Telegram polling запущен: @<имя бота>.`
- либо, если что-то не задано (API при этом работает):
  `Telegram-бот не запущен: Telegram:BotToken не задан (переменная TELEGRAM_BOT_TOKEN). Площадка работает без него.`
  или `Telegram-бот не запущен: Telegram:AdminChatId не задан (переменная TELEGRAM_ADMIN_CHAT_ID), …`
- если `TELEGRAM_ENABLE_UPDATE_POLLING=false`: `Telegram polling выключен.`

Плохие признаки — разобраться до шага 10:

| Строка в логе | Что значит |
|---|---|
| `Схема базы данных отстаёт от кода: не применено миграций …` | шаг 6 не выполнен |
| `Telegram отклонил токен бота (код 401)…` | неверный/отозванный `TELEGRAM_BOT_TOKEN` |
| `Telegram 409: обновления забирает другой инстанс или выставлен webhook …` | тот же токен опрашивается ещё где-то; выключите там или поставьте `TELEGRAM_ENABLE_UPDATE_POLLING=false` |
| `Telegram-бот остановлен из-за ошибки. API продолжает работать.` | смотрите стек исключения рядом |
| `Не удалось записать переход на объявление …` | нет таблицы `link_clicks` или прав на неё (шаги 6–7); редирект при этом работает |

### 9.3. Редирект из канала

```bash
ID=$(curl -s 'http://127.0.0.1:8090/api/listings?limit=1' | grep -o '"id":"[0-9a-f-]\{36\}"' | head -1 | cut -d'"' -f4); echo "listing: $ID"

# Активное объявление → 302 на карточку с UTM
curl -s -o /dev/null -D - "http://127.0.0.1:8090/r/l/$ID?s=tg" | grep -iE '^(HTTP|location|cache-control)'

# Несуществующее → 302 на главную
curl -s -o /dev/null -D - "http://127.0.0.1:8090/r/l/00000000-0000-0000-0000-000000000000?s=tg" | grep -iE '^(HTTP|location)'

# То же снаружи, через туннель
curl -s -o /dev/null -D - "https://api.genesis-hq.com/r/l/$ID?s=tg" | grep -iE '^(HTTP|location)'
```

Ожидается для активного: `HTTP/1.1 302`,
`Location: https://market.genesis-hq.com/listing/<slug>?utm_source=telegram&utm_medium=channel&utm_campaign=listing`,
`Cache-Control: no-store`. Для несуществующего: `Location: https://market.genesis-hq.com/`.

Переход записан (повтор с того же IP в течение 60 с не пишется — это дедупликация):

```bash
docker exec genesis-postgres psql -U genesis -d genesis -c \
  'SELECT "Source", count(*), max("CreatedAt") FROM link_clicks GROUP BY "Source";'
```

### 9.4. Очередь публикации наполняется

Строка появляется, когда модератор одобряет объявление, или когда доверенный автор публикует
объявление без модерации:

```bash
docker exec genesis-postgres psql -U genesis -d genesis -c \
  'SELECT "Status", count(*), min("EnqueuedAt"), max("EnqueuedAt") FROM channel_post_queue GROUP BY "Status";'

docker exec genesis-postgres psql -U genesis -d genesis -c \
  'SELECT q."Status", q."EnqueuedAt", q."AttemptCount", q."LastError", l."Title"
     FROM channel_post_queue q JOIN listings l ON l."Id" = q."ListingId"
    ORDER BY q."EnqueuedAt" DESC LIMIT 10;'
```

При выключенном флаге все строки остаются `Pending` — так и должно быть.

---

## 10. Включение публикации в канал (когда проверка пройдена)

Перед включением: бот должен быть **администратором канала** с правом публиковать и редактировать
сообщения.

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
sed -i 's/^TELEGRAM_PUBLISHING_ENABLED=.*/TELEGRAM_PUBLISHING_ENABLED=true/' .env
grep '^TELEGRAM_PUBLISHING_ENABLED=' .env
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --no-build api
sleep 20; docker logs --since 2m genesis-api 2>&1 | grep -E 'ChannelPublishing|Telegram-канал|TELEGRAM_CHANNEL_ID|tzdata'
```

Ожидается: `Публикация в Telegram-канал запущена: окно 09:00–21:00 (Europe/Chisinau), не чаще раза в 20 мин.`
Первый пост — в ближайшую минуту, если сейчас окно и очередь не пуста:
`Объявление <id> опубликовано в канал, message_id <n>`.

**Проверьте первый пост в канале глазами.** Фото передаётся ссылкой на WebP
(`https://api.genesis-hq.com/api/images/…`); документация Bot API формат WebP для `sendPhoto` по URL
не гарантирует. Если вместо поста в логе
`Публикация объявления … в канал не удалась (попытка 1, статус Failed)`, посмотрите причину:

```bash
docker exec genesis-postgres psql -U genesis -d genesis -c \
  "SELECT \"ListingId\", \"AttemptCount\", \"LastError\" FROM channel_post_queue WHERE \"Status\" = 'Failed' ORDER BY \"EnqueuedAt\" DESC LIMIT 5;"
```

При `400` с текстом про тип содержимого/URL — выключите публикацию обратно (шаг 11.1): каждый
следующий пост упадёт так же. Вернуть упавшие в очередь после исправления:

```bash
docker exec genesis-postgres psql -U genesis -d genesis -c \
  "UPDATE channel_post_queue SET \"Status\" = 'Pending', \"AttemptCount\" = 0, \"LastError\" = NULL WHERE \"Status\" = 'Failed';"
```

---

## 11. Откат

### 11.1. Только остановить публикацию (без отката кода)

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
sed -i 's/^TELEGRAM_PUBLISHING_ENABLED=.*/TELEGRAM_PUBLISHING_ENABLED=false/' .env
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --no-build api
```

Новые посты перестают уходить сразу. Очередь сохраняется.

### 11.2. Откат кода, схема остаётся (рекомендуемый)

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
git checkout "$(cat ~/genesis-pre-telegram.commit)"
docker tag genesis-pridnestrovie-market-server-api:pre-telegram "$(cat ~/genesis-pre-telegram.image)"
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --no-build api
until curl -fs -o /dev/null http://127.0.0.1:8090/health/live; do sleep 2; done
curl -s -o /dev/null -w 'ready: %{http_code}\n' http://127.0.0.1:8090/health/ready
./scripts/smoke-deploy.sh
```

Сервер остаётся на detached HEAD — вернуться к `master` при следующем выкате: `git checkout master`.

**Безопасна ли новая схема для старого кода — да, проверено по миграции:**

- `listings."TelegramPublishedAt"` — `timestamp with time zone` **без `NOT NULL`** и без значения по
  умолчанию. Старый код колонку не знает: при вставке в `listings` она получает `NULL`, чтение её
  не выбирает.
- `channel_post_queue` и `link_clicks` — новые таблицы, старый код к ним не обращается. Внешний ключ
  `channel_post_queue → listings` с `ON DELETE CASCADE` ничего не запрещает (объявления удаляются мягко).
- Проверка готовности старого кода смотрит только миграции, которых **нет в БД**; лишняя миграция в
  истории её не валит — `/health/ready` остаётся 200.

### 11.3. Откат схемы (только если действительно нужно)

Выполнять **до** `git checkout` из 11.2: `migrate.sh` собирает проект из текущего каталога, и
отменить миграцию может только код, в котором она есть.

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
./scripts/migrate.sh 20260913142037_AddListingRejectionReason
```

Удаляет таблицы `channel_post_queue`, `link_clicks` и колонку `listings."TelegramPublishedAt"`
**вместе с данными**: очередь, статистика переходов и время публикации постов теряются безвозвратно.
Затем — шаги из 11.2.

### 11.4. Полное восстановление из бэкапа шага 1 (крайний случай)

Теряются **все** изменения в БД после шага 1 — новые пользователи, объявления, отзывы.

```bash
cd /home/ceo_andrey/Genesis-Pridnestrovie-Market-SERVER
DUMP=$(ls -1t backups/genesis-genesis-*.dump | head -1); echo "$DUMP"
docker stop genesis-api
docker exec -i genesis-postgres pg_restore -U genesis -d genesis --clean --if-exists --no-owner < "$DUMP"
```

Затем — шаги из 11.2 (код и образ).

### Что откат не возвращает

- **Посты в Telegram-канале** остаются: бот их не удаляет. Подписи, отредактированные на
  «✅ ПРОДАНО» / «⛔ Снято с публикации», тоже остаются.
- **Сообщения**, которые бот уже отправил пользователям и админу.
- **Строки `outbox_messages`**, записанные новым кодом. Среди них возможны правки поста с пометкой
  `active`. Старый код такую пометку не знает и обработает её по-своему: если у объявления есть пост
  в канале и задан `TELEGRAM_BOT_TOKEN`, перепишет подпись поста простым текстом без кнопки и без
  HTML-разметки. На работу площадки это не влияет, только на вид поста.
- **`.env`**: добавленные переменные остаются. Старому коду они не мешают; полностью вернуть файл —
  из копии `.env.bak.<время>` шага 2.
- **Данные в новых таблицах** при откате по 11.2 сохраняются и подхватятся при повторном выкате: строки
  `Pending` будут опубликованы (воркер перед постом перепроверяет, что объявление всё ещё активно).
