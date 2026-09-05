# Инвентаризация инфраструктуры

Проход S0 «Прогона по безопасности сервера». Составлено 2026-09-05 по состоянию
репозитория на коммите `6976887`. Только перечисление, без анализа — выводы
в `docs/server-security-pass-2026-09-05.md` и `docs/server-security-log.md`.

> **Актуализировано после исправлений того же дня.** Таблицы ниже описывают
> топологию **после** закрытия находок: прод-стек больше не публикует наружу
> ни одного порта кроме прокси, сервиса `adminer` в нём нет. Что именно
> изменилось и почему — в журнале находок.

Разобранные источники: `docker-compose.yml`, `docker-compose.dev.yml`, `Dockerfile`,
`.dockerignore`, `.env.example`, `.env.dev.example`, `src/GenesisMarket.Api/appsettings.json`,
`src/GenesisMarket.Api/appsettings.Development.json`, `Makefile`, `scripts/*.sh`.

Обратный прокси — `deploy/Caddyfile` + overlay `docker-compose.proxy.yml`.
CI — `.github/workflows/ci.yml`. Оба добавлены при закрытии находок №6 и №18;
на момент прохода в репозитории их не было.

---

## 1. Сервисы

### Прод — `docker-compose.yml`

| Сервис | Контейнер | Образ (тег) | Команда запуска | Restart policy |
|---|---|---|---|---|
| `api` | `genesis-api` | локальная сборка из `Dockerfile` (final: `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`) | `ENTRYPOINT ["dotnet", "GenesisMarket.Api.dll"]` | `unless-stopped` |
| `postgres` | `genesis-postgres` | `postgres:17-alpine` | по умолчанию (`postgres`) | `unless-stopped` |
| `minio` | `genesis-minio` | `minio/minio@sha256:14cea493…8936e` (закреплён по digest) | `server /data --console-address ":9001"` | `unless-stopped` |
| `caddy` (overlay `docker-compose.proxy.yml`) | `genesis-caddy` | `caddy:2.10-alpine` | по умолчанию, конфиг `deploy/Caddyfile` | `unless-stopped` |

Сервиса `adminer` в прод-стеке нет намеренно. У всех сервисов задан `logging`
(json-file, `max-size: 10m`, `max-file: 5`).

Прод-файл защищён guard-полем `x-guard: "${I_CONFIRM_PROD_LAUNCH:?...}"` (`:17`) —
любая команда `docker compose` без этой переменной завершается ошибкой.

Hardening прод-`api`: `read_only: true`, `tmpfs: /tmp`, `security_opt: no-new-privileges:true`,
`cap_drop: ALL`, `mem_limit: 512m`, `cpus: 1.0`, `pids_limit: 256`.
`no-new-privileges:true` есть у всех сервисов; лимиты `mem_limit`/`cpus`/`pids_limit` —
у api (512m), postgres (1g), minio (1g) и caddy (256m). У caddy `cap_drop: ALL`
плюс единственная нужная `cap_add: NET_BIND_SERVICE` (порты ниже 1024).

### Dev — `docker-compose.dev.yml` (имя проекта `genesis-market-dev`)

| Сервис | Образ (тег) | Команда запуска | Restart policy |
|---|---|---|---|
| `migrator` (`genesis-migrator-dev`) | `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | `dotnet tool install dotnet-ef` → `dotnet restore` → `dotnet ef database update` | нет (одноразовый) |
| `api` (`genesis-api-dev`) | `mcr.microsoft.com/dotnet/sdk:10.0-alpine` | `dotnet watch run --project src/GenesisMarket.Api/... --no-launch-profile` | `unless-stopped` |
| `postgres` (`genesis-postgres-dev`) | `postgres:17-alpine` | по умолчанию | `unless-stopped` |
| `minio` (`genesis-minio-dev`) | `minio/minio@sha256:14cea493…8936e` | `server /data --console-address ":9001"` | `unless-stopped` |
| `adminer` (`genesis-adminer-dev`) | `adminer:5.4.0` | по умолчанию | `unless-stopped` |

Полностью изолирован от прода: своё имя проекта, своя сеть, свои volumes, все
переменные — через `env_file: .env.dev` (без `${...}`-подстановки, чтобы забытый
`--env-file` не подтянул прод-секреты из корневого `.env`).

---

## 2. Порты

### Прод — `docker-compose.yml`

| Сервис | Внутренний порт | Внешний порт | Интерфейс | Проброшен наружу |
|---|---|---|---|---|
| `api` | 8080 (`ASPNETCORE_HTTP_PORTS`) | `${API_HOST_PORT:-8090}` | **`127.0.0.1`** | нет (только loopback) |
| `postgres` | 5432 | — | — | нет (секции `ports` нет) |
| `minio` (S3 API) | 9000 | — | — | нет |
| `minio` (Console) | 9001 | — | — | нет |
| `caddy` (overlay) | 80, 443, 443/udp | 80, 443, 443/udp | `0.0.0.0` | **да — единственный** |

Единственный сервис, смотрящий в интернет, — обратный прокси. Порт 80 нужен не
только для редиректа на HTTPS, но и для ACME HTTP-01, иначе сертификат не
выпустится. API доступен только с самой машины, поэтому прокси нельзя обойти —
а вместе с ним нельзя обойти ни TLS, ни подсчёт rate-limit по реальному IP.

Без overlay (например, при публикации через ngrok) туннель поднимается на
`127.0.0.1:8090` — `ngrok http 8090` подключается к loopback (`docs/ngrok-public-api.md`).
До прода к БД ходят через `docker exec -it genesis-postgres psql`, а не через
проброшенный порт.

### Dev — `docker-compose.dev.yml`

| Сервис | Внутренний порт | Внешний порт | Интерфейс | Проброшен наружу |
|---|---|---|---|---|
| `api` | 8080 | 8091 | `0.0.0.0` | да (`:74-75`) |
| `postgres` | 5432 | 5435 | `0.0.0.0` | да (`:94-95`) |
| `minio` (S3 API) | 9000 | 9002 | `0.0.0.0` | да (`:114-115`) |
| `minio` (Console) | 9001 | 9003 | `0.0.0.0` | да (`:114-116`) |
| `adminer` | 8080 | 8082 | `0.0.0.0` | да (`:127-128`) |
| `migrator` | — | — | — | нет |

---

## 3. Тома и сети

### Тома — прод

| Том | Монтируется | Что хранит |
|---|---|---|
| `genesis-postgres` | `postgres:/var/lib/postgresql/data` | данные PostgreSQL |
| `genesis-caddy-data` (overlay) | `caddy:/data` | сертификаты и ключи ACME |
| `genesis-caddy-config` (overlay) | `caddy:/config` | рабочий конфиг Caddy |
| `genesis-minio` | `minio:/data` | объекты MinIO: `listings/` (фото объявлений), `avatars/` |

Bind-mount в прод-стеке два, оба read-only: `./scripts/create-app-role.sql` в postgres и `./deploy/Caddyfile` в caddy. `/var/run/docker.sock` не монтируется никуда.
ФС контейнера `api` — `read_only: true` плюс `tmpfs: /tmp`.

### Тома — dev

| Том | Монтируется | Что хранит |
|---|---|---|
| bind `.:/src` | `migrator`, `api` | исходники репозитория (hot reload) |
| `genesis-dev-postgres` | `postgres:/var/lib/postgresql/data` | данные dev-БД |
| `genesis-dev-minio` | `minio:/data` | объекты dev-MinIO |
| `genesis-dev-nuget` | `/root/.nuget/packages` | кэш NuGet |
| `genesis-dev-dotnet-tools` | `/root/.dotnet/tools` | глобальные dotnet-инструменты (`dotnet-ef`) |
| `genesis-dev-{domain,infra,api}-{bin,obj}` | соответствующие `bin`/`obj` | артефакты сборки, вынесены из bind-mount |

### Сети

| Сеть | Драйвер | Стек | Кто в ней |
|---|---|---|---|
| `genesis` | bridge | прод | `api`, `postgres`, `minio`, и `caddy` при overlay |
| `genesis-dev` | bridge | dev | `migrator`, `api`, `postgres`, `minio`, `adminer` |

Внутри каждой сети все сервисы видят друг друга по имени
(`api → postgres:5432`, `api → minio:9000`). Между прод- и dev-сетями связи нет.

### Каталоги на хосте

| Путь | Назначение | В `.gitignore`? |
|---|---|---|
| `./backups` | дампы `pg_dump` и архивы MinIO от `scripts/backup.sh` (chmod 700) | да |
| `./db-backups` | разовый дамп схемы; выведен из индекса git | да |

---

## 4. Переменные окружения

Читаются сервисом `api` (прод, `docker-compose.yml:30-99`), если не указано иное.
«Секрет» = значение, утечка которого требует ротации.

| Переменная | Назначение | Секрет |
|---|---|---|
| `I_CONFIRM_PROD_LAUNCH` | guard прод-compose; задаётся только в команде, не в `.env` | нет |
| `ASPNETCORE_ENVIRONMENT` | окружение ASP.NET Core | нет |
| `ASPNETCORE_HTTP_PORTS` | порт Kestrel внутри контейнера | нет |
| `LOG_LEVEL` → `Serilog__MinimumLevel__Default` | уровень логирования | нет |
| `POSTGRES_DB` | имя БД (читают `api` и образ `postgres`) | нет |
| `POSTGRES_USER` | владелец схемы; под ним идут ТОЛЬКО миграции (образ `postgres`, `scripts/migrate.sh`) | нет |
| `POSTGRES_APP_USER` | ограниченная роль приложения (только DML) | нет |
| `POSTGRES_APP_PASSWORD` | пароль роли приложения | **да** |
| `POSTGRES_PASSWORD` | пароль БД (читают `api` и образ `postgres`) | **да** |
| `POSTGRES_HOST` / `POSTGRES_PORT` | адрес БД для внешних инструментов | нет |
| `POSTGRES_HOST_PORT` | хост-порт БД; в проде не используется (порт не публикуется) | нет |
| `MINIO_ROOT_USER` → `Minio__AccessKey` | ключ доступа MinIO (читают `api` и образ `minio`) | **да** |
| `MINIO_ROOT_PASSWORD` → `Minio__SecretKey` | секретный ключ MinIO (читают `api` и образ `minio`) | **да** |
| `MINIO_BUCKET` | имя бакета | нет |
| `MINIO_USE_SSL` | TLS до MinIO | нет |
| `MINIO_ENDPOINT` | адрес MinIO для внешних инструментов | нет |
| `JWT_SECRET` → `Jwt__Key` | ключ подписи HS256 | **да** |
| `JWT_ISSUER` / `JWT_AUDIENCE` | issuer/audience токенов | нет |
| `JWT_EXPIRES_DAYS` | срок жизни токена | нет |
| `IPHASH_KEY` → `Security__IpHashKey` | ключ HMAC для хеширования IP | **да** |
| `SMTP_HOST` / `SMTP_PORT` | SMTP-сервер писем подтверждения | нет |
| `SMTP_USER` | учётка SMTP | **да** |
| `SMTP_PASSWORD` | пароль SMTP | **да** |
| `SMTP_FROM` / `SMTP_FROM_NAME` / `SMTP_USE_SSL` | параметры отправителя | нет |
| `RESEND_API_KEY` → `Resend__ApiKey` | ключ API Resend | **да** |
| `RESEND_FROM_EMAIL` | адрес отправителя Resend | нет |
| `FEEDBACK_NOTIFICATION_EMAIL` | куда шлются обращения формы обратной связи | нет |
| `TELEGRAM_BOT_TOKEN` → `Telegram__BotToken` | токен Telegram-бота | **да** |
| `TELEGRAM_BROADCAST_CHAT_ID` | общий канал для постов | нет |
| `TELEGRAM_WEB_BASE_URL` | адрес фронтенда для ссылок в постах | нет |
| `Telegram__CategoryChannels__<category>` | маршрутизация «категория → канал» | нет |
| `SEO_WEB_BASE_URL` / `SITE_BASE_URL` | публичный адрес сайта для канонических ссылок | нет |
| `SEO_SITE_NAME` | название сайта в мета-тегах | нет |
| `PUBLISHING_REQUIRED_VERIFICATION` | что обязательно для публикации | нет |
| `CORS_ALLOWED_ORIGINS` → `Cors__AllowedOrigins` | белый список origin | нет |
| `TRUSTED_PROXY_NETWORKS` → `Network__KnownNetworks` | CIDR доверенных прокси | нет |
| `TRUSTED_PROXY_IPS` → `Network__KnownProxies` | IP доверенных прокси | нет |
| `TRUSTED_PROXY_FORWARD_LIMIT` → `Network__ForwardLimit` | глубина цепочки XFF | нет |
| `RATE_LIMIT_GLOBAL_PER_MINUTE` | глобальный лимит на IP | нет |
| `RATE_LIMIT_SEARCH_PER_MINUTE` | лимит поиска на IP | нет |
| `RATE_LIMIT_SENSITIVE_ANON_PER_HOUR` | лимит чувствительных анонимных POST | нет |
| `RATE_LIMIT_CREATE_LISTING_PER_HOUR` | лимит создания объявлений | нет |
| `RATE_LIMIT_FEEDBACK_ANON_PER_HOUR` / `..._USER_PER_HOUR` | лимиты формы обратной связи | нет |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | адрес OTLP-коллектора | нет |
| `API_HOST_PORT` | порт API на 127.0.0.1 (ADMINER_HOST_PORT больше не используется) | нет |
| `PUBLIC_DOMAIN` | домен для сертификата Caddy (overlay) | нет |
| `ACME_EMAIL` | почта для уведомлений Let's Encrypt (overlay) | нет |
| `KESTREL_MAX_BODY_BYTES` и др. `KESTREL_*` | лимиты запроса и таймауты Kestrel | нет |
| `BACKUP_RETENTION_DAYS` | сколько суток хранить дампы (`scripts/backup.sh`) | нет |
| `BACKUP_DIR` | каталог дампов (`scripts/backup.sh`, `restore-check.sh`) | нет |
| `COMPOSE_DIR` | каталог с compose-файлом (все скрипты) | нет |
| `IMAGE` | образ для сканирования (`scripts/check-image-secrets.sh`) | нет |
| `PG_IMAGE` | образ для проверки восстановления (`restore-check.sh`) | нет |

Дополнительно в dev (`.env.dev`, читается через `env_file:`): `ENV`,
`DATABASE_URL`, `DATABASE_URL_HOST` (строки подключения для psql/adminer — секрет),
`GENESIS_DESIGN_CONNECTION` (design-time подключение для `dotnet ef` — секрет),
плюс те же переменные под именами ASP.NET Core (`Postgres__*`, `Minio__*`,
`Jwt__Key`, `Security__IpHashKey`, `Smtp__*`, `Telegram__*`, `Seo__*`,
`Cors__AllowedOrigins`, `Network__*`, `RateLimit__*`).

Несекретные значения по умолчанию живут в `src/GenesisMarket.Api/appsettings.json`
(секции `Serilog`, `Jwt`, `Bcrypt`, `Verification`, `Listings`, `CatalogHygiene`,
`Scheduling`, `Outbox`, `SavedSearch`, `Telegram`, `Seo`, `Phone`, `ContactReveal`);
все секретные ключи в нём пустые, это проверяется при старте
(`Configuration/OptionsValidationSetup.cs`).

---

## 5. Внешние зависимости

Куда сервер ходит наружу.

| Зависимость | Направление | Протокол | Креденшл | Где настроено |
|---|---|---|---|---|
| Resend (письма по обращениям формы обратной связи) | исходящее | HTTPS, `https://api.resend.com/` | `RESEND_API_KEY` (Bearer) | `Feedback/FeedbackServiceCollectionExtensions.cs:23-25`, `Feedback/ResendEmailService.cs` |
| Telegram Bot API (уведомления и посты об объявлениях) | исходящее | HTTPS, `api.telegram.org` | `TELEGRAM_BOT_TOKEN` в URL | `Outbox/Telegram/HttpTelegramClient.cs` |
| SMTP-сервер (письма с кодом подтверждения e-mail) | исходящее | SMTP/TLS, `SMTP_HOST:SMTP_PORT` | `SMTP_USER` + `SMTP_PASSWORD` | `Auth/EmailSender.cs` (`SmtpEmailSender`) |
| OTLP-коллектор (трейсы и метрики) | исходящее | OTLP/gRPC | нет | `Observability/ObservabilitySetup.cs`; выключено, `OTEL_EXPORTER_OTLP_ENDPOINT` пуст |
| ngrok (публикация API наружу) | входящее | HTTPS → HTTP на хост-порт 8090 | authtoken агента ngrok (вне репозитория) | `docs/ngrok-public-api.md` |
| Docker Hub / MCR (образы) | исходящее, только при сборке | HTTPS | нет (анонимно) | `docker-compose*.yml`, `Dockerfile` |
| nuget.org (пакеты) | исходящее, только при сборке | HTTPS | нет (анонимно) | `Dockerfile:18` (`dotnet restore`) |

Эквайринга и платёжных провайдеров в проекте нет — сделки офлайн.
SMS-провайдера нет: `ISmsSender` реализован заглушкой, пишущей в лог.
