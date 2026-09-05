# Журнал находок по безопасности сервера

Дополняется при каждом прогоне (`server_security_pass.md`). **Ничего не удаляется** —
закрытые записи остаются со статусом «закрыто» и датой.

Приоритет = «кто может использовать» (1-3) + «что он получит» (1-3).
**6** — чинить сегодня · **4-5** — на этой неделе · **2-3** — в бэклог с датой.

Статус «**принят риск**» — легитимный исход: осознанно решено не чинить сейчас,
причина записана.

---

## Прогон 2026-09-05 (найдено на HEAD `6976887`)

Проходы S0-S7, S10, S11 — по репозиторию (Claude Code). S8 — частично.
S9 и SSH-части S1/S8/S10 не выполнялись. Отметки: `docs/server-security-pass-2026-09-05.md`.

**Итог: 24 находки. Закрыто в коде 21, принят риск 2, осталось открытым 1
(ротация пароля — действие вне репозитория).**

| № | Дата | Проход | Что нашли | Где (файл:строка / хост) | Кто может | Что получит | Приоритет | Статус | Дата закрытия |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 2026-09-05 | S3 | **Пароль продовой PostgreSQL в публичном GitHub.** Значение `Postgres.Password` дословно совпадало с `POSTGRES_PASSWORD` из живого `.env`. Репозиторий public, файл читался по raw-ссылке без авторизации (проверено, HTTP 200). В git с `3a7973c` (2026-08-10) | `src/GenesisMarket.Api/appsettings.Development.json:15` | 3 | 3 | **6** | **открыто — требуется ротация** | |
| 2 | 2026-09-05 | S1 | **PostgreSQL опубликован на `0.0.0.0:5434`.** Вместе с №1 — прямой вход в БД из интернета. Docker пробрасывает порт через iptables в обход ufw | `docker-compose.yml:133-138` | 3 | 3 | **6** | закрыто | 2026-09-05 |
| 3 | 2026-09-05 | S1 | **Adminer в прод-compose**, опубликован на `0.0.0.0:8081`. Веб-клиент БД не должен существовать в проде | `docker-compose.yml:162-173` | 3 | 3 | **6** | закрыто | 2026-09-05 |
| 4 | 2026-09-05 | S6 | **EXIF/GPS не снимался с аватаров.** Байты клались в MinIO как пришли, без `IImageProcessor`; аватар отдаётся анонимно по `GET /api/users/{id}/avatar`. Обходной путь загрузки в обход снятия EXIF | `Controllers/MeController.cs:82-112`; выдача — `Controllers/UsersController.cs:54-70` | 3 | 3 | **6** | закрыто | 2026-09-05 |
| 5 | 2026-09-05 | S2 | **API открыт на `0.0.0.0:8090` без TLS**, ngrok обходился обращением к порту напрямую. Доверенная сеть XFF — весь `172.16.0.0/12`, поэтому прямой клиент мог подставить `X-Forwarded-For` и обойти rate-limit по IP | `docker-compose.yml:100-102,87`; `Security/NetworkSetup.cs:28-41` | 3 | 2 | 5 | закрыто | 2026-09-05 |
| 6 | 2026-09-05 | S2 | **Reverse proxy и TLS в проекте отсутствовали.** Нет Caddyfile/nginx.conf, нет редиректа HTTP→HTTPS, версии TLS вне контроля проекта | нет файла; `Program.cs` | 3 | 2 | 5 | закрыто | 2026-09-05 |
| 7 | 2026-09-05 | S3 | **`appsettings.Development.json` попадал в прод-образ.** `COPY . .` + `Microsoft.NET.Sdk.Web` включает `appsettings.*.json` в publish; `.dockerignore` его не исключал. `check-image-secrets.sh` проверял только `appsettings.json` и эту утечку не ловил | `Dockerfile:21`, `.dockerignore`, `scripts/check-image-secrets.sh:37-38` | 1 | 3 | 4 | закрыто | 2026-09-05 |
| 8 | 2026-09-05 | S10 | **Dev-заглушки логирования выбирались только по пустоте конфига, без проверки окружения.** `DevSmsSender` был зарегистрирован всегда ⇒ номер телефона и код подтверждения уходили в stdout и в Production | `Auth/AuthServiceCollectionExtensions.cs:52-56`, `Auth/SmsSender.cs:18`, `Auth/EmailSender.cs:97`, `Feedback/FeedbackServiceCollectionExtensions.cs:17-19` | 1 | 3 | 4 | закрыто | 2026-09-05 |
| 9 | 2026-09-05 | S7 | **Пустой `ASPNETCORE_ENVIRONMENT` отключал прод-валидацию.** Compose подставлял `${ASPNETCORE_ENVIRONMENT}` без дефолта; пустая строка — не Production, поэтому не проверялись дефолтный пароль БД, пустой CORS и `Security:IpHashKey` | `docker-compose.yml:31`, `Configuration/OptionsValidationSetup.cs:46` | 1 | 3 | 4 | закрыто | 2026-09-05 |
| 10 | 2026-09-05 | S8 | **`scripts/backup.sh` не отработал бы из cron.** Guard `x-guard: "${I_CONFIRM_PROD_LAUNCH:?...}"` срабатывает на любой команде compose, включая `exec`. Плюс: не было бэкапа MinIO, копии без ограничения прав | `docker-compose.yml:17`, `scripts/backup.sh:38` | 1 | 3 | 4 | закрыто (частично — см. «Осталось руками») | 2026-09-05 |
| 11 | 2026-09-05 | S5 | **Приложение подключалось к PostgreSQL под суперпользователем.** `POSTGRES_USER` — та же роль, что создаёт образ `postgres:17-alpine`, т.е. superuser с `CREATEDB`/`CREATEROLE` | `docker-compose.yml:41,123-125` | 1 | 3 | 4 | закрыто (нужен разовый прогон скрипта на проде) | 2026-09-05 |
| 12 | 2026-09-05 | S5 / S8 | **`db-backups/` и `backups/` не в `.gitignore`.** В репозитории лежал дамп схемы (данных в нём нет — проверено); следующий дамп с данными ушёл бы в публичный git | `.gitignore`, `db-backups/*.sql` | 3 | 3 (потенц.) | 4 | закрыто | 2026-09-05 |
| 13 | 2026-09-05 | S10 | **Телефон хранится в открытом виде** (`users.PhoneE164`, `varchar(20)`), а не HMAC-хэшем. Хэшируются только IP. Модель угроз документа прохода расходится с реальностью | `Domain/Entities/User.cs:20`, `Persistence/Configurations/UserConfiguration.cs:27` | 1 | 3 | 4 | **принят риск** | 2026-09-05 |
| 14 | 2026-09-05 | S7 | **Лимиты Kestrel не заданы:** `MaxRequestBodySize`, `MaxRequestHeadersTotalSize`, таймауты — дефолты (тело 30 МБ) | нигде; ср. `ListingImagesController.cs:56-57` | 3 | 1 | 4 | закрыто | 2026-09-05 |
| 15 | 2026-09-05 | S10 | **Ротация логов Docker не настроена.** Ни в одном compose нет `logging:`; `json-file` растёт до заполнения диска. Алертов нет, OTLP-экспорт выключен | `docker-compose.yml`, `docker-compose.dev.yml` | 3 | 1 | 4 | закрыто (алерты — нет, см. ниже) | 2026-09-05 |
| 16 | 2026-09-05 | S2 | **Нет `Permissions-Policy`; отдавался `Server: Kestrel`** (`AddServerHeader = false` не задан) | `Security/SecurityHeadersMiddleware.cs:22-27`; `Program.cs` | 3 | 1 | 4 | закрыто | 2026-09-05 |
| 17 | 2026-09-05 | S7 | **`/health/ready` публичен и отдавал детальный JSON** со статусами зависимостей (`UIResponseWriter`) — разведка стека без авторизации | `Program.cs:147-151` | 3 | 1 | 4 | закрыто | 2026-09-05 |
| 18 | 2026-09-05 | S11 | **CI/CD отсутствовал полностью.** Нет `.github/workflows`, значит нет gitleaks, нет проверки уязвимых зависимостей, нет автозапуска тестов. Именно поэтому находка №1 прожила месяц | нет каталога `.github/` | 1 | 2 | 3 | закрыто | 2026-09-05 |
| 19 | 2026-09-05 | S4 | **Плавающие теги образов:** `minio/minio:latest`, `adminer:latest` | `docker-compose.yml:143,163`; `docker-compose.dev.yml:102,122` | 1 | 2 | 3 | закрыто | 2026-09-05 |
| 20 | 2026-09-05 | S4 | **Лимиты ресурсов только у `api`.** У `postgres`, `minio`, `adminer` нет `mem_limit`/`cpus`/`pids_limit` | `docker-compose.yml:111-113` | 2 | 1 | 3 | закрыто | 2026-09-05 |
| 21 | 2026-09-05 | S3 / S11 | **`bin/` и `obj/` закоммичены** — 970 файлов, включая копии `appsettings.Development.json` (дублировали находку №1) | `.gitignore`, `src/*/bin/**`, `tests/*/bin/**` | 3 | 1 | 4 | закрыто | 2026-09-05 |
| 22 | 2026-09-05 | S5 | **В проде не было шага миграций.** Автомиграций при старте нет (правильно), но и отдельного шага деплоя тоже не было — на пустой базе API не поднимается вовсе (Quartz ищет `qrtz_*`) | `docker-compose.yml`; ср. `docker-compose.dev.yml:17-45` | 1 | 1 | 2 | закрыто | 2026-09-05 |
| 23 | 2026-09-05 | S6 | Приложение ходит в MinIO под **root-креденшлами**, а не под service-аккаунтом с политикой на один бакет. Смягчено тем, что MinIO не публикуется наружу и бакет приватный | `docker-compose.yml:45-46` | 1 | 2 | 3 | **принят риск** | 2026-09-05 |
| 24 | 2026-09-05 | S11 | **Уязвимые зависимости в сборке** (обнаружено при первом же запуске `dotnet build`): `System.Security.Cryptography.Xml` 10.0.0 — 8 advisories High, прямая ссылка в Infrastructure; `OpenTelemetry.*` 1.10.0 — Moderate; `SSH.NET` 2024.1.0 — High (транзитивно из Testcontainers, только тесты). NuGet-аудит был понижен до предупреждения и в глаза не бросался | `Directory.Build.props:12`, `GenesisMarket.Infrastructure.csproj:23`, `GenesisMarket.Api.csproj:33-38` | 2 | 3 | 5 | закрыто | 2026-09-05 |

---

## Что сделано по каждой закрытой находке

**Сеть и доступ (№2, №3, №5, №6).**
`docker-compose.yml`: у `postgres` и `minio` секции `ports` нет вообще; сервис
`adminer` из прод-файла удалён (остался в dev на 8082); API публикуется как
`127.0.0.1:${API_HOST_PORT}` вместо `0.0.0.0`. Добавлены `deploy/Caddyfile` и
`docker-compose.proxy.yml` — Caddy как единственный сервис, смотрящий наружу:
автоматический сертификат Let's Encrypt, редирект HTTP→HTTPS, TLS 1.2+
(TLS 1.0/1.1 Caddy не поддерживает в принципе), HSTS на уровне прокси.
Конфигурация проверена `caddy validate` — `Valid configuration`.

**Секреты (№1, №7, №12, №21).**
Пароль убран из `appsettings.Development.json`. Валидатор конфигурации теперь
сканирует и файл окружения Development (раньше был исключён), поэтому вернуть
секрет в этот файл нельзя — приложение не стартует. `.dockerignore` исключает
`**/appsettings.*.json`; `.gitignore` — `bin/`, `obj/`, `backups/`, `db-backups/`;
970 файлов сборки убраны из индекса (`git rm --cached`, файлы на диске целы).
`check-image-secrets.sh` расширен: проверяет все `appsettings*.json`, отсутствие
конфигураций окружений в образе и что процесс не root.

**Загрузка изображений (№4).**
`MeController.UploadAvatar` проходит через тот же `IImageProcessor`, что и фото
объявлений: тип по magic bytes, лимит пикселей, снятие EXIF/IPTC/XMP,
перекодирование в WebP — всё до `storage.PutAsync`. Закреплено двумя тестами:
`Uploaded_avatar_with_gps_has_no_exif_after_processing` и
`Avatar_upload_rejects_content_that_is_not_an_image`.

**Fail-fast конфигурации (№8, №9).**
Валидатор требует, чтобы `ASPNETCORE_ENVIRONMENT` был одним из
Development/Staging/Production; compose дополнительно требует переменную через
`:?`. Лог-заглушки отправителей (`DevSmsSender`, `LogEmailSender`,
`LogResendEmailService`) регистрируются только в Development; вне его пустой
`Smtp:Host` или `Resend:ApiKey` — ошибка старта. SMS-провайдера нет, поэтому вне
Development зарегистрирован `UnavailableSmsSender`: канал честно отвечает 503
(`SendStatus.ChannelUnavailable`) вместо записи номера и кода в лог.

**База данных (№11, №22).**
`scripts/create-app-role.sql` создаёт роль с правами только на DML (плюс default
privileges на будущие таблицы), идемпотентен. Compose использует
`POSTGRES_APP_USER`/`POSTGRES_APP_PASSWORD` с откатом на `POSTGRES_USER`, пока
роль не заведена. `scripts/migrate.sh` — отдельный шаг деплоя под владельцем
схемы, в одноразовом SDK-контейнере.

**Контейнеры и логи (№14, №15, №16, №17, №19, №20).**
Лимиты Kestrel из секции `Kestrel:Limits`, `AddServerHeader = false`,
`Permissions-Policy`, `/health/ready` без детализации зависимостей. Во всех
compose-файлах `logging` с `max-size`/`max-file`; `mem_limit`/`cpus`/`pids_limit`
у postgres, minio и caddy. Образы закреплены: MinIO по digest (у него нет
стабильных semver-тегов), adminer — `5.4.0`, caddy — `2.10-alpine`.

**Бэкапы (№10).**
`backup.sh` обращается к контейнерам через `docker exec` по `container_name`
(guard прод-файла больше не мешает cron), добавлен бэкап объектов MinIO,
проверка что контейнеры запущены, `chmod 600` на дампы и `chmod 700` на каталог.

**Цепочка поставки (№18, №24).**
`.github/workflows/ci.yml`: gitleaks по полной истории, отдельная проверка
appsettings, сборка с `TreatWarningsAsErrors`, `dotnet list package --vulnerable`
(роняет сборку), тесты, сборка образа + `check-image-secrets.sh`, и smoke-тест
на реально поднятом стеке. Триггер `pull_request` (не `pull_request_target`),
`permissions: contents: read`, все actions закреплены по SHA (проверены через
GitHub API). Уязвимые пакеты обновлены: `System.Security.Cryptography.Xml`
10.0.0 → 10.0.11, OpenTelemetry 1.10.0 → 1.18.0, Testcontainers 4.1.0 → 4.14.0
(последнее убрало уязвимый `SSH.NET`). Сборка: 0 предупреждений, 0 ошибок.

---

## Принятые риски

**№13 — телефон в открытом виде.** Хэшировать нельзя: номер обязан быть
обратимым, его отдают покупателю при раскрытии контактов
(`ListingsController.cs:312`) и из него строятся ссылки `tel:`/Viber/WhatsApp
(`Listings/ContactLinkBuilder.cs`). HMAC-хэш убил бы саму функцию. Посылка
документа прохода («телефоны хранятся как HMAC-хэши») в этом проекте неверна —
хэшируются только IP. Смягчено: номер не отдаётся в публичном профиле, раскрытие
контактов под rate-limit и пишется в `contact_reveals`, в логах маскируется.
Если понадобится защита на уровне хранения — это шифрование колонки с ключом
из окружения, отдельная задача.

**№23 — root-креденшлы MinIO.** Отдельный service-аккаунт с политикой на бакет
требует либо `mc admin` в контуре развёртывания, либо ручного шага при каждом
пересоздании тома. MinIO наружу не публикуется, бакет приватный, доступ к нему
есть только у API по внутренней сети. Пересмотреть, если появится второй
потребитель хранилища.

---

## Осталось руками (вне репозитория)

1. **Ротировать пароль PostgreSQL (№1) — единственное, что осталось открытым.**
   Значение месяц лежало в публичном репозитории и могло быть проиндексировано;
   удаление коммита ничего не меняет. Порядок: сменить пароль в БД
   (`ALTER ROLE ... PASSWORD ...`), обновить `.env`, перезапустить стек.
   Остальные секреты проверены по всей истории и **чисты**: `JWT_SECRET`,
   `IPHASH_KEY`, `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `SMTP_USER`,
   `SMTP_PASSWORD`, `RESEND_API_KEY`, `CORS_ALLOWED_ORIGINS` — 0 совпадений.
2. **Завести роль приложения на проде (№11):** прогнать
   `scripts/create-app-role.sql`, заполнить `POSTGRES_APP_USER`/`POSTGRES_APP_PASSWORD`
   в `.env`. Пока они пусты, API работает суперпользователем.
3. **Выгрузка бэкапов на внешнее хранилище (№10):** копии по-прежнему лежат на
   той же машине и не шифруются. Плюс поставить cron и хотя бы раз прогнать
   `scripts/restore-check.sh`, записав дату сюда.
4. **Алерты (№15):** ротация логов есть, оповещения при падении контейнера или
   заполнении диска — нет. Дешёвый вариант: healthcheck-пинг в тот же
   Telegram-бот, который уже настроен.
5. **Проход S9 (доступ к серверу)** и SSH-части S1/S8/S10 — не выполнялись.
6. **Сузить `TRUSTED_PROXY_NETWORKS`** до подсети docker-сети после запуска Caddy
   (сейчас доверяется весь `172.16.0.0/12`).

## Не проверено

- Проход **S9** целиком; SSH-части **S1** (`ss -tulpn`, `ufw`, `iptables`),
  **S8** (наличие cron, проверка восстановления), **S10** (`docker logs`, `df -h`).
- Права файла `.env` на сервере (`stat -c '%a %U' .env`, норма `600`).
- Расхождение фактической конфигурации сервера с той, что в git — само по себе находка.
