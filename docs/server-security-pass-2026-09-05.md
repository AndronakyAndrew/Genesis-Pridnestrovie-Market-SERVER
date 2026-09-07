# Прогон по безопасности сервера — отметки

**Дата:** 2026-09-05 · **Ветка:** `master` · **HEAD:** `6976887`
**Исполнитель:** Claude Code (проходы 🖥️ CC) · **Источник:** `server_security_pass.md`

Область: только репозиторий и его конфигурация. Продовый сервер не трогался — всё
помеченное 🔐 SSH остаётся за человеком.

> **Историческая справка, документ не переписывается.** Упоминания ngrok ниже
> верны на 2026-09-05. С 2026-09-07 публикация идёт через Cloudflare Tunnel,
> а `docs/ngrok-public-api.md` переименован в `docs/cloudflare-tunnel.md`.
> Запись об изменении периметра — в конце `docs/server-security-log.md`.

**Результат проверки:** пройдено 9 проходов из 12, найдено 24 расхождения.
Инвентарь — `docs/infra-inventory.md`, находки и их статусы — `docs/server-security-log.md`.

**Результат исправления (в тот же день):** закрыто 21, принято как риск 2,
осталось открытым 1. Ниже отметки описывают состояние **на момент обнаружения** —
так и задумано, журнал не переписывается задним числом. Актуальные статусы
и перечень того, что осталось сделать руками, — в `docs/server-security-log.md`.

> ⛔ **Единственное, что осталось и делается вне репозитория:** ротация пароля
> PostgreSQL. Он месяц лежал в **публичном** GitHub
> (`src/GenesisMarket.Api/appsettings.Development.json:15`) — из файла он убран,
> но старое значение уже могло быть проиндексировано. Остальные секреты проверены
> по всей истории и чисты.

**Проверено на реально поднятом стеке** (изолированный проект, продовый файл
`docker-compose.yml`): `scripts/smoke-deploy.sh` — 19 проверок из 19 пройдено;
образ прошёл `scripts/check-image-secrets.sh`; миграции и создание ограниченной
роли БД отработали, API поднялся под ролью с правами только на DML; все три
fail-fast (пустое окружение, отсутствие SMTP и Resend в Production) сработали.

---

## Сводка по проходам

| Проход | Тема | Тип | Статус | Находок | Приоритет худшей |
|---|---|---|---|---|---|
| S0 | Инвентаризация инфраструктуры | 🖥️ CC | ✅ выполнен | — | — |
| S1 | Сетевой периметр | 🖥️ CC | ✅ выполнен (CC-часть) | 3 | **6** |
| S2 | TLS и reverse proxy | 🖥️ CC | ✅ выполнен | 4 | 5 |
| S3 | Секреты в окружении и образах | 🖥️ CC | ✅ выполнен | 4 | **6** |
| S4 | Hardening контейнеров | 🖥️ CC | ✅ выполнен | 3 | 3 |
| S5 | PostgreSQL | 🖥️ CC | ✅ выполнен | 2 | 4 |
| S6 | MinIO и файловое хранилище | 🖥️ CC | ✅ выполнен | 1 | **6** |
| S7 | Конфигурация в продакшене | 🖥️ CC | ✅ выполнен | 3 | 4 |
| S8 | Бэкапы и восстановление | 🔐 SSH | 🟡 частично (по репозиторию) | 2 | 4 |
| S9 | Доступ к серверу | 🔐 SSH | ⬜ не выполнен | — | — |
| S10 | Логи и мониторинг | 🖥️ CC + 🔐 SSH | ✅ выполнен (CC-часть) | 3 | 4 |
| S11 | Цепочка поставки и CI/CD | 🖥️ CC | ✅ выполнен | 1 | 3 |
| — | После прохода: закрепить | 🖥️ CC | ⬜ не выполнен | — | — |

---

## S0. Инвентаризация инфраструктуры 🖥️ CC — ✅

- [x] Разобраны `docker-compose.yml`, `docker-compose.dev.yml`, `Dockerfile`,
      `.dockerignore`, `.env.example`, `.env.dev.example`, `appsettings*.json`,
      `Makefile`, `scripts/*.sh`.
- [x] Пять списков сохранены в **`docs/infra-inventory.md`**.
- [x] Конфигов reverse proxy (nginx/Caddy/traefik) в репозитории **нет**.
- [x] Файлов CI/CD (`.github/workflows`) в репозитории **нет**.

**Решение по таблице портов** («должен ли порт быть доступен из интернета?»):

| Порт (прод) | Биндинг | Должен быть в интернете? | Вердикт |
|---|---|---|---|
| API 8090→8080 | `0.0.0.0` | нет — только через прокси на 443 | → журнал №8 |
| PostgreSQL 5434→5432 | `0.0.0.0` | **нет** | → журнал №2 |
| Adminer 8081→8080 | `0.0.0.0` | **нет, и его вообще не должно быть в проде** | → журнал №3 |
| MinIO 9000/9001 | не публикуется | нет | ✅ норма |

---

## S1. Сетевой периметр 🖥️ CC — ✅ (CC-часть)

- [x] **1. Биндинги.** Все три `ports:` в `docker-compose.yml` — короткая форма
      `"host:container"` без адреса ⇒ Docker слушает `0.0.0.0`:
      api `:100-102`, postgres `:133-138`, adminer `:169-171`.
- [x] **2. Кому `ports` не нужны.** MinIO API и Console — ✅ уже без `ports`
      (`:158`, комментарий «наружу не публикуем»). PostgreSQL — ❌ опубликован.
      Adminer — ❌ опубликован.
- [x] **3. Adminer в проде.** ❌ Есть, сервис `adminer` в `docker-compose.yml:162-173`
      — не в override, не под профилем. → журнал №3.
- [x] **4. Разделение окружений.** ✅ Есть отдельный `docker-compose.dev.yml`
      (изолированное имя проекта `genesis-market-dev`, свои volumes и сеть,
      переменные через `env_file:`, а не `${...}` — грамотно). Прод-файл
      защищён guard-переменной `I_CONFIRM_PROD_LAUNCH` (`:17`) — но у неё есть
      побочный эффект, см. журнал №12.
- [x] **5. Фронтенд напрямую в MinIO/БД.** ✅ Нет. Байты стримятся через API
      (`Controllers/ImagesController.cs:31`, `Controllers/UsersController.cs:66`),
      presigned-ссылки на MinIO наружу не отдаются, бакет приватный
      (`Storage/MinioObjectStorage.cs:79-80`).
- [ ] 🔐 **SSH-проверка не выполнена** — нужно руками на сервере:
      `sudo ss -tulpn | grep LISTEN`, `sudo ufw status verbose`,
      `sudo iptables -S | grep DOCKER`. Docker публикует порты в обход ufw —
      единственная надёжная защита для 5434/8081 это убрать `ports`.

**Находки:** №2 (PostgreSQL наружу), №3 (Adminer в проде), №8 (API без TLS/прокси).

---

## S2. TLS и reverse proxy 🖥️ CC — ✅

- [x] **1. Reverse proxy.** ❌ Отсутствует. В репозитории нет ни Caddyfile, ни
      nginx.conf, ни traefik. Kestrel слушает `0.0.0.0:8090` напрямую
      (`docker-compose.yml:100-102`). Публичный доступ сейчас — туннель ngrok
      (`docs/ngrok-public-api.md`), TLS терминирует ngrok. → журнал №9.
- [x] **2. HTTPS/сертификат.** ❌ В проекте не настроен — целиком на стороне ngrok,
      автопродление вне контроля репозитория.
- [x] **3. Редирект HTTP→HTTPS и HSTS.** Редиректа нет (`UseHttpsRedirection` в
      `Program.cs` отсутствует). HSTS есть, но условный:
      `Security/SecurityHeadersMiddleware.cs:30-31`, `max-age=63072000; includeSubDomains`
      — выставляется только при `Request.IsHttps`, т.е. только когда доверенный прокси
      прислал `X-Forwarded-Proto: https`. `preload` нет (и правильно, пока домен не финальный).
- [x] **4. Версии TLS.** Вне контроля проекта (терминация у ngrok). При переходе на
      свой прокси — зафиксировать TLS 1.2+.
- [x] **5. ForwardedHeaders.** ✅ Настроен и **ограничен**:
      `Security/NetworkSetup.cs:23-42` — `KnownProxies`/`KnownIPNetworks` очищаются и
      заполняются строго из конфигурации, `ForwardLimit` по умолчанию 1.
      ⚠️ Но доверенная сеть по умолчанию — весь `172.16.0.0/12`
      (`docker-compose.yml:87`, `.env.example:108`), а порт API открыт на `0.0.0.0`.
      Любой, кто достучится до `:8090` в обход ngrok, приходит с адреса docker-моста —
      т.е. из доверенной сети — и может подставить `X-Forwarded-For`. → журнал №8.
- [x] **6. Заголовки ответа.** `X-Content-Type-Options: nosniff` ✅,
      `X-Frame-Options: DENY` ✅, `Referrer-Policy: no-referrer` ✅,
      `Content-Security-Policy` ✅ (`default-src 'none'; frame-ancestors 'none'` для API,
      ослабленный для `/swagger`) — всё в `SecurityHeadersMiddleware.cs:22-27`.
      `Permissions-Policy` ❌ отсутствует. → журнал №16.
- [x] **7. Раскрытие версии.** `X-Powered-By`/`X-AspNet-Version` ✅ нет.
      `Server: Kestrel` ❌ отдаётся — `AddServerHeader = false` нигде не задан. → журнал №16.

---

## S3. Секреты в окружении и образах 🖥️ CC — ✅

- [x] **1. ENV/ARG с реальными значениями.** ✅ В `Dockerfile` нет ни одного `ARG`/`ENV`
      с секретом. В `docker-compose.yml` все значения — подстановки `${...}` из `.env`.
- [x] **2. `.env` в `.gitignore`.** ✅ `.gitignore:34` (`.env*`). `.env.example` и
      `.env.dev.example` в репозитории, все секретные поля пустые — ✅ норма.
- [x] **3. История git.** ❌ **НАЙДЕНО.**
      `src/GenesisMarket.Api/appsettings.Development.json:15` содержит пароль PostgreSQL,
      **дословно совпадающий** с `POSTGRES_PASSWORD` из живого `.env`. Файл в git
      с первого коммита (`3a7973c`, 2026-08-10), присутствует во всех текущих коммитах
      ветки. Репозиторий `AndronakyAndrew/Genesis-Pridnestrovie-Market-SERVER` —
      **публичный** (`"visibility": "public"`, проверено), файл читается по raw-ссылке
      без авторизации (HTTP 200). → журнал №1.
      Там же `minioadmin/minioadmin` — но живой MinIO использует другие креды, это
      только dev-значение.
      ✅ Проверены на утечку и **чисты**: `JWT_SECRET`, `IPHASH_KEY`, `MINIO_ROOT_USER`,
      `MINIO_ROOT_PASSWORD`, `SMTP_USER`, `SMTP_PASSWORD`, `RESEND_API_KEY`,
      `CORS_ALLOWED_ORIGINS` — 0 совпадений во всей истории.
      ✅ Файла `.env` в истории нет.
- [x] **4. Секреты внутрь образа через `COPY . .`.** ❌ **НАЙДЕНО.** `Dockerfile:21`
      копирует весь контекст; `.dockerignore` исключает `.env*`, но **не** исключает
      `appsettings.*.json`. `Microsoft.NET.Sdk.Web` включает `appsettings.*.json`
      в publish ⇒ `appsettings.Development.json` с паролем едет в финальный слой.
      → журнал №4.
- [x] **5. `.dockerignore`.** Существует, исключает `bin/`, `obj/`, `.git/`, `.env*`,
      `*.md`, `logs/`. ❌ Не исключает `appsettings.*.json`. → журнал №4.
- [x] **6. Логирование конфигурации при старте.** ✅ Не логируется. Есть даже защита
      в глубину: `Security/MaskingDestructuringPolicy.cs` маскирует `password`, `token`,
      `phone`, `email`, `key`, `secret` при деструктуризации `{@obj}`.
- [x] **7. Секреты в бандле фронтенда.** N/A — репозиторий серверный, фронтенд отдельный.

---

## S4. Hardening контейнеров 🖥️ CC — ✅

- [x] **1. Пользователь.** ✅ `Dockerfile:31` — `USER $APP_UID` (uid 1654 из образов .NET),
      не root.
- [x] **2. Теги образов.** Смешанно: `postgres:17-alpine` ✅,
      `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0-alpine` ✅,
      **`minio/minio:latest`** (`:143`) ❌, **`adminer:latest`** (`:163`) ❌. → журнал №14.
- [x] **3. Многоэтапная сборка.** ✅ `Dockerfile:10` (sdk, build) → `:27` (aspnet, final).
      В финальном образе только `/app/publish`, ни SDK, ни исходников, ни NuGet-кэша.
- [x] **4. `/var/run/docker.sock`.** ✅ Не монтируется нигде.
- [x] **5. `privileged` / `cap_add` / `network_mode: host`.** ✅ Нет ни одного.
      Наоборот: `cap_drop: ALL` (`:109-110`), `no-new-privileges:true` у всех четырёх
      сервисов.
- [x] **6. Healthcheck и restart.** `restart: unless-stopped` — у всех четырёх ✅.
      Healthcheck: api ✅ (`Dockerfile:37-38`), postgres ✅ (`:128-132`),
      minio ✅ (`:153-157`), adminer ❌ нет. → журнал №15.
- [x] **7. Лимиты ресурсов.** Только у api: `mem_limit: 512m`, `cpus: 1.0`,
      `pids_limit: 256` (`:111-113`). У postgres, minio, adminer — ❌ нет. → журнал №15.
- [x] **8. Тома read-only.** ✅ У api вся ФС `read_only: true` + `tmpfs: /tmp` (`:104-106`)
      — образцово. Тома данных postgres/minio по определению нужны на запись.

---

## S5. PostgreSQL 🖥️ CC — ✅

- [x] **1. Роль подключения.** ❌ **НАЙДЕНО.** Приложение ходит под `${POSTGRES_USER}`
      (`docker-compose.yml:41`), и это тот же `POSTGRES_USER`, что передан образу
      `postgres:17-alpine` (`:124`). В официальном образе эта роль создаётся как
      **суперпользователь**. Отдельной роли с DML-правами нет. → журнал №13.
- [x] **2. Права роли.** Следствие п.1 — все: `SUPERUSER`, `CREATEDB`, `CREATEROLE`,
      владение схемой `public`, создание расширений.
- [x] **3. Пароль БД.** Длина 48 hex-символов ✅, берётся из `.env` ✅, не дефолтный ✅.
      Старт в Production падает на дефолтном/пустом
      (`Configuration/OptionsValidationSetup.cs:24,49-53`). ❌ Но он утёк — см. №1.
- [x] **4. Автомиграции в проде.** ✅ **Нет.** `Database.Migrate()` вызывается только в
      тестах (`tests/.../AuthApiFactory.cs:61`, `SchedulerIntegrationTests.cs:48`).
      В `Program.cs` миграций нет. Отдельный сервис `migrator` есть только в
      `docker-compose.dev.yml:17-45`. ⚠️ Обратная сторона: в проде шага миграций
      нет вообще. → журнал №21.
- [x] **5. SQL-скрипты с сид-данными.** Один файл — `db-backups/genesis_pre_AddSavedSearches_20260812_122922.sql`.
      Проверено: **все `COPY` пустые**, 0 bcrypt-хэшей, 0 e-mail — это дамп схемы без
      данных, тестовых пользователей и дефолтных админов в нём нет ✅.
      ⚠️ Но каталог `db-backups/` не в `.gitignore`. → журнал №11.
- [x] **6. `log_statement = all`.** ✅ Не найдено в конфигурации: `postgres:17-alpine`
      запускается без своего `postgresql.conf` и без `command:`, дефолт — `log_statement = none`.

---

## S6. MinIO и файловое хранилище 🖥️ CC — ✅

- [x] **1. Креденшлы.** ✅ Не дефолтные: `MINIO_ROOT_USER`/`MINIO_ROOT_PASSWORD` из
      `.env` (12 и 22 символа, не `minioadmin`). В `appsettings.Development.json:19-20`
      стоит `minioadmin/minioadmin`, но это локальное dev-значение, с живым MinIO
      не совпадает.
- [x] **2. Root или service-пользователь.** ⚠️ Приложение использует **root-креды**
      (`docker-compose.yml:45-46` → `Minio__AccessKey/SecretKey`). Отдельного
      service-аккаунта с политикой на один бакет нет. Смягчено тем, что MinIO не
      опубликован наружу. → журнал №20.
- [x] **3. Политика бакета.** ✅ Приватный. `Storage/MinioObjectStorage.cs:71-81` —
      бакет создаётся, публичная политика **не** ставится, комментарий это фиксирует.
- [x] **4. Как фронтенд получает изображения.** ✅ Проксированием через API:
      `GET /api/images/listings/{listingId}/{file}` (`Controllers/ImagesController.cs:22-38`)
      и `GET /api/users/{id}/avatar` (`Controllers/UsersController.cs:54-70`).
      Прямых публичных URL и presigned-ссылок наружу нет.
- [x] **5. Presigned на клиенте.** ✅ Не применяется вообще
      (`GetPresignedUrlAsync` есть в интерфейсе, но не вызывается ни из одного контроллера).
      Ключей MinIO на клиенте нет.
- [x] **6. Пайплайн загрузки.** ✅ Тип по magic bytes
      (`Imaging/ImageSharpImageProcessor.cs:44-57`, `DetectFormatAsync`, whitelist
      JPEG/PNG/WEBP), размеры по заголовку до декодирования (`:59-72`, лимит 50 Мпикс),
      лимит аллокации декодера 512 МБ (`:29-37`), лимит файла 10 МБ и
      `RequestSizeLimit`/`RequestFormLimits` (`Controllers/ListingImagesController.cs:27-30,56-57,72-73`),
      не больше 8 на объявление (`:75-78`). Имя файла из запроса **не используется** —
      ключ генерирует сервер (`:100-103`), путь с `../` невозможен. На выдаче ключ
      дополнительно проверяется строгим regex (`ImagesController.cs:17-19,25-26`).
- [x] **7. EXIF/GPS.** ⚠️ **Половина.**
      ✅ Для фото объявлений — снимается на входе, до сохранения:
      `Imaging/ImageSharpImageProcessor.cs:92-94` (`ExifProfile`/`IptcProfile`/`XmpProfile = null`),
      после `AutoOrient()` и до кодирования; вызов — `ListingImagesController.cs:87`,
      строго до `storage.PutAsync` на `:106-108`.
      ❌ **Для аватаров — не снимается.** `Controllers/MeController.cs:82-112`:
      байты кладутся в MinIO **как пришли**, без `IImageProcessor`; в самом коде
      комментарий «Полная обработка (ресайз, снятие EXIF, WebP) — шаг 8» — шаг не сделан.
      Аватар отдаётся анонимно. **Обходной путь загрузки в обход снятия EXIF существует.**
      → журнал №5.
- [x] **8. Служебные файлы в том же бакете.** Бакет один, префиксы `listings/` и
      `avatars/`. Политика одна (приватная), выдача — только через два контроллера
      с проверкой формы ключа. Служебных файлов в бакете нет ✅.

---

## S7. Конфигурация приложения в продакшене 🖥️ CC — ✅

- [x] **1. `ASPNETCORE_ENVIRONMENT`.** ❌ **Риск есть.** `docker-compose.yml:31`
      подставляет `${ASPNETCORE_ENVIRONMENT}` **без** значения по умолчанию. Если
      переменная в `.env` пуста или отсутствует, контейнер стартует с **пустой**
      строкой окружения — это не «Development», но и не `Production`, поэтому весь
      блок `if (environment.IsProduction())` в `Configuration/OptionsValidationSetup.cs:46`
      **пропускается**: не проверяются ни дефолтный пароль БД, ни пустой CORS, ни
      `Security:IpHashKey`. Fail-fast на незаданное окружение отсутствует.
      Сейчас в `.env` стоит `Production` ✅, но защита держится только на этом. → журнал №7.
- [x] **2. `UseDeveloperExceptionPage`.** ✅ Явно не вызывается. Обработка исключений —
      `GlobalExceptionHandler`, стектрейс/тип исключения добавляются в ответ **только**
      при `environment.IsDevelopment()` (`Middleware/GlobalExceptionHandler.cs:44-50`),
      иначе наружу уходит только `traceId`.
- [x] **3. Swagger/OpenAPI.** ✅ Генерация регистрируется при `!IsProduction()`
      (`Program.cs:104-108`), UI поднимается **только** при `IsDevelopment()`
      (`:122-126`). В Production недоступен.
      ⚠️ При пустом окружении (п.1) генератор регистрируется, но UI и эндпоинт
      `/swagger/v1/swagger.json` — нет, т.к. `UseSwagger()` под `IsDevelopment()`.
- [x] **4. CORS.** ✅ Явный белый список из конфигурации (`Program.cs:41-49`),
      `AllowAnyOrigin` не используется нигде. `AllowCredentials` есть, но только
      вместе с `WithOrigins(...)` — запрещённого сочетания нет. В Production пустой
      список валится на старте (`OptionsValidationSetup.cs:61-71`), там же проверяется
      абсолютность URI и отсутствие завершающего слэша.
- [x] **5. `/dev/mock-payment/*`.** ✅ **Таких эндпоинтов в проекте нет.** Поиск по
      `mock-payment`, `/dev/` во всём `src/` — 0 совпадений. Эквайринга нет, сделки
      офлайн. Отвечает 404 просто потому, что маршрута не существует.
      ⚠️ Требование документа «показать явную проверку окружения, а не отсутствие
      роутинга» здесь неприменимо — кода нет. Но соседнее требование остаётся в силе:
      fail-fast на незаданное `ASPNETCORE_ENVIRONMENT` нужен → журнал №7.
- [x] **6. Другие dev/debug/seed-эндпоинты.** Проверено: во всём API `IsDevelopment()`
      встречается в 2 местах (`Program.cs:122`, `GlobalExceptionHandler.cs:44`),
      `IsProduction()` — в 2 (`Program.cs:104`, `OptionsValidationSetup.cs:46`).
      Debug- и seed-эндпоинтов нет. Публичные без авторизации:
      `/health/live`, `/health/ready` (`Program.cs:141-151`, явный `AllowAnonymous`).
      ⚠️ `/health/ready` отдаёт детальный JSON со статусами зависимостей. → журнал №19.
      Остальное закрыто `FallbackPolicy` (требует аутентификации по умолчанию).
- [x] **7. Kestrel-лимиты.** ❌ `MaxRequestBodySize`, `MaxRequestHeadersTotalSize`,
      `KeepAliveTimeout`, `RequestHeadersTimeout` — не заданы нигде, работают дефолты
      (тело 30 МБ). Точечные лимиты есть только на загрузке файлов
      (`ListingImagesController.cs:56-57`, `MeController.cs:83`). → журнал №17.
- [x] **8. Production-сборка фронтенда и source maps.** N/A — серверный репозиторий.

---

## S8. Бэкапы и восстановление 🔐 SSH — 🟡 частично

Проверено по репозиторию и скриптам (это доступно CC). Фактическое наличие cron,
последних копий и их восстановимость — за человеком.

- [x] **1. Автоматический бэкап PostgreSQL.** Скрипт есть: `scripts/backup.sh`
      (`pg_dump -Fc` через `docker compose exec`, ротация по `BACKUP_RETENTION_DAYS`,
      проверка «дамп подозрительно мал»). Расписание **не установлено скриптом** —
      строка crontab только в комментарии (`:8-9`).
      ❌ **Скрипт в текущем виде не отработает:** `docker-compose.yml:17` содержит
      `x-guard: "${I_CONFIRM_PROD_LAUNCH:?...}"`, а Compose интерполирует файл на
      **любой** команде, включая `exec`. Без переменной `docker compose exec postgres`
      падает ⇒ `set -euo pipefail` обрывает бэкап. → журнал №12.
- [x] **2. Бэкап MinIO.** ❌ Отсутствует. Изображения объявлений не резервируются.
- [x] **3. Где хранятся.** ❌ `BACKUP_DIR="./backups"` (`backup.sh:27`) — та же машина,
      тот же каталог рядом с compose. Off-site копии нет.
      ⚠️ `backups/` и `db-backups/` не в `.gitignore` → журнал №11.
- [x] **4. Шифрование и доступ.** ❌ Дампы не шифруются, права файлов не выставляются.
- [x] **5. Ретенция и ротация.** ✅ Есть: `-mtime +${BACKUP_RETENTION_DAYS}` (по умолчанию
      14 суток), удаление старых с логом (`backup.sh:48-50`).
- [x] **6. Документированная процедура восстановления.** ⚠️ Полу-есть: скрипт
      `scripts/restore-check.sh` (одноразовый контейнер, `pg_restore`, sanity-запросы,
      снос в конце) — грамотный, но отдельного документа в `docs/` нет.
- [ ] 🔐 **Не выполнено (SSH):** проверить, что cron реально стоит; запустить
      `./scripts/restore-check.sh` и записать дату проверки.
      *Бэкап, который ни разу не восстанавливали, бэкапом не считается.*

---

## S9. Доступ к серверу 🔐 SSH — ⬜ не выполнен

Claude Code сюда доступа не имеет и не должен иметь. Выполнить руками и вписать
результат в `docs/server-security-log.md`:

| Пункт | Команда | Норма | Результат |
|---|---|---|---|
| Вход по паролю | `sudo sshd -T \| grep -i passwordauthentication` | `no` | |
| Вход под root | `sudo sshd -T \| grep -i permitrootlogin` | `no` | |
| Список ключей | `cat ~/.ssh/authorized_keys` | только свои, каждый опознан | |
| Кто в sudo/docker | `getent group sudo docker` | только ты | |
| Автообновления | `systemctl status unattended-upgrades` | active | |
| Уязвимости ОС | `sudo apt list --upgradable` | пусто/некритичное | |
| Брутфорс SSH | `sudo fail2ban-client status sshd` | установлен и работает | |
| Кто заходил | `last -n 30` | только ожидаемые входы | |

Плюс из S3: проверить права `.env` — `stat -c '%a %U' .env` должно быть `600`.

---

## S10. Логи и мониторинг 🖥️ CC — ✅ (CC-часть)

- [x] **1. Куда пишутся.** stdout контейнера, Serilog в JSON
      (`RenderedCompactJsonFormatter`, `Program.cs:21-36`). Внешнего стока нет.
      Есть correlation id (`Middleware/RequestIdEnricherMiddleware.cs`) и
      `UseSerilogRequestLogging()`.
- [x] **2. Ротация логов Docker.** ❌ **Отсутствует.** Ни в `docker-compose.yml`, ни в
      `docker-compose.dev.yml` нет секции `logging:` с `max-size`/`max-file`.
      Драйвер по умолчанию `json-file` без ограничений ⇒ логи растут до заполнения
      диска. → журнал №22.
- [x] **3. Что попадает в логи.**
      ✅ Защита в глубину: `Security/MaskingDestructuringPolicy.cs:19-26` маскирует
      `password`, `token`, `refreshtoken`, `phone`, `phonee164`, `email`,
      `passwordhash`, `securitystamp`, `key`, `secret` при `{@obj}`.
      ✅ Неудачный вход e-mail не пишет (`Security/SecurityAudit.cs:54-56`).
      ✅ Ошибки: полные детали только в лог, наружу — `traceId`.
      ❌ **Но dev-заглушки отправителей выбираются только по пустоте конфига, без
      проверки окружения**, и пишут PII открытым текстом:
      `Auth/SmsSender.cs:18` — `[DEV SMS] Кому: {Phone} | Текст: {Message}`
      (номер + код подтверждения; `DevSmsSender` зарегистрирован **всегда**,
      `Auth/AuthServiceCollectionExtensions.cs:52`);
      `Auth/EmailSender.cs:97` — адрес + тело письма с кодом, если `Smtp:Host` пуст (`:53-54`);
      `Feedback/LogResendEmailService.cs:13-15` — имя, контакт и текст обращения,
      если `Resend:ApiKey` пуст (`Feedback/FeedbackServiceCollectionExtensions.cs:17-19`).
      Маскирование здесь не помогает: значения передаются отдельными аргументами,
      а не деструктуризацией. → журнал №6.
      ⚠️ Заодно: `users.PhoneE164` хранится **в открытом виде** (`varchar(20)`,
      `Persistence/Configurations/UserConfiguration.cs:27`), не HMAC-хэшем.
      Хэшируются только IP (`Auth/IpHasher.cs`). Модель угроз документа прохода здесь
      расходится с реальностью. → журнал №18.
- [x] **4. События безопасности.** ✅ Отдельный журнал `Area="security"`,
      `Security/SecurityAudit.cs`: `login.success`, `login.failed`, `password.changed`,
      `moderation.action`, `role.changed`, `resource.forbidden`, `rate_limited`.
      Идентификаторы — `userId` и `ipHash`, сырой IP не пишется.
      ⚠️ Не покрыты: отзыв токена по `SecurityStamp` и отклонённый вебхук
      (вебхуков в проекте нет). Блокировка аккаунта покрывается `moderation.action`.
- [x] **5. Алерты.** ❌ Отсутствуют полностью. Ни при падении контейнера, ни при
      заполнении диска. OpenTelemetry инструментация есть
      (`Observability/ObservabilitySetup.cs`), но `OTEL_EXPORTER_OTLP_ENDPOINT` в
      `.env` не задан ⇒ экспорт выключен, коллектора нет. → журнал №22.
- [ ] 🔐 **SSH-часть не выполнена:** `docker logs --tail 200 genesis-api` глазами на
      предмет PII, `df -h` по разделу с `/var/lib/docker`.

---

## S11. Цепочка поставки и CI/CD 🖥️ CC — ✅

- [x] **1-6. CI/CD отсутствует полностью.** Каталога `.github/` в репозитории нет,
      ни одного workflow. Следовательно:
      секретов в пайплайне нет (нечему течь) · шага печати env нет ·
      `pull_request_target` нет · пиннинга actions нет ·
      **gitleaks нет** · **`dotnet list package --vulnerable` нет** · `npm audit` N/A.
      Деплой — руками (`docker compose up -d --build`, `docs/ngrok-public-api.md`).
      Именно отсутствие проверки секретов в CI позволило находке №1 прожить месяц.
      → журнал №10.
- [x] Что **есть** и работает как замена части CI:
      `scripts/check-image-secrets.sh` — проверяет историю слоёв и `appsettings.json`
      образа. ⚠️ Проверяет **только** `appsettings.json`, поэтому находку №4
      (`appsettings.Development.json` в образе) он **не ловит** — `:37-38`.
      `Directory.Build.props:8` — `TreatWarningsAsErrors`, NuGet-аудит виден как
      предупреждение, но сборку не роняет (`:12`).

---

## После прохода: закрепить — ✅ выполнен

- [x] **1. Правила в `CLAUDE.md`** — раздел «Правила инфраструктуры и безопасности
      сервера», 16 строк, по строке на класс находок.
- [x] **2. Smoke-тест деплоя** — `scripts/smoke-deploy.sh`, 19 проверок на реально
      поднятом стеке (конфигурация не мокается): живость · `/health/ready` без
      раскрытия состава стека · Swagger недоступен по трём путям · пять заголовков
      безопасности · отсутствие `Server`/`X-Powered-By`/`X-AspNet-Version` ·
      порты 5434/9000/9001/8081 закрыты · CORS не принимает чужой origin.
      Пункт «`/dev/mock-payment/*` отдаёт 404» опущен осознанно — таких
      эндпоинтов в проекте нет вовсе (см. S7.5).
- [x] **3. Запуск в CI** — `.github/workflows/ci.yml`, job `smoke` зависит от
      `build` и `image`: поднимает прод-стек, прогоняет миграции, заводит
      ограниченную роль БД и выполняет smoke-тест. Плюс jobs `secrets` (gitleaks
      по полной истории) и `build` (уязвимые зависимости роняют сборку).

---

## Оговорки

- Отсутствие находок означает «не нашли», а не «их нет».
- Проверка шла **по репозиторию**. Реальная конфигурация на сервере может отличаться
  от той, что в git — само расхождение будет находкой. Проходы S9 и SSH-части
  S1/S8/S10 не выполнены.
- Перед приёмом реальных платежей нужен внешний пентест; этот прогон его не заменяет.
