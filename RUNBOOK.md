# RUNBOOK — выкатка обновления сервера

Порядок для обновления «служебные роли + 2FA + журнал + справочники в БД + приглашения
продавцов». Команды — на серверном ноутбуке, из корня репозитория. **Прод трогает только
человек, по этому списку**; из dev-окружения миграции к проду не применяются.

Короткое правило порядка: **бэкап БД → миграции → права роли → бэкенд → проверка → фронтенд.**
Фронтенд идёт последним: новые экраны ходят в ручки, которых нет у старого API.

## 0. Перед выкаткой

### Новые миграции (применяются по порядку)

| Миграция | Что делает |
|---|---|
| `20260928105704_StaffRolesAuditTwoFactor` | `project_role_assignments` (роль переезжает из `users.Role`), `audit_events`, `role_changes`, `user_two_factor`, `mfa_challenges`, отметка 2FA у сессий; триггер `journal_append_only` |
| `20260928111628_CatalogReferencesToTables` | Города и категории — таблицы `cities`, `categories` вместо PostgreSQL enum; колонки объявлений/профилей/поисков — на код-строку с FK |
| `20260928161915_SellerInvitations` | `seller_invitations`, `invitation_drafts`, `invitation_draft_photos`, `publication_consents` (только на добавление); `listings.SourceInvitationId`/`PreparedByUserId`; `users.HasPassword` (по умолчанию `true`) |
| `20260928182022_TwoFactorLockout` | `user_two_factor.FailedAttempts` (int, 0) и `LockedUntil` (timestamptz, null) — блокировка второго фактора по аккаунту после серии неверных кодов. Только добавление колонок со значениями по умолчанию: существующие строки не меняются, блокировок после выкатки нет |

Порядок выкатки не меняется: бэкап → миграции (все четыре по порядку) → права роли → бэкенд → проверка → фронтенд.
Фронтенд после бэкенда обязательно: новая карточка жалобы передаёт `?reportId=` в ручку контактов, а старый
бэкенд этот параметр игнорирует — наоборот (старый фронтенд с новым бэкендом) модератор просто не увидит
контакты вне жалобы, что и требуется.

Проверить, что прод стоит на предыдущей миграции (понадобится для отката):

```bash
source scripts/load-env.sh; load_env .env
docker exec genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1;'
```

### Переменные окружения (`.env` на сервере)

| Переменная | Обязательна | Зачем |
|---|---|---|
| `Security__TotpKey` | **да** — без неё API не стартует | Ключ шифрования секретов 2FA, 32 байта base64: `openssl rand -base64 32`. Хранить в менеджере паролей: смена ключа делает все 2FA нечитаемыми (потом только `reset-2fa` каждому) |
| `Seo__WebBaseUrl` | да (уже задана) | Из неё строится ссылка приглашения `https://market.genesis-hq.com/invite/…`; пустая — выпуск ссылки отвечает 503 |
| `Invitations__LinkLifetimeDays` | нет, 14 | Срок ссылки приглашения |
| `Invitations__ClosedRetentionDays` | нет, 30 | Удаление «Истекло»/«Отозвано» |
| `Invitations__AbandonedDraftDays` | нет, 60 | Удаление брошенных «Готовится» |
| `Invitations__ConsentVersion` | нет, `2026-10-01` | Версия текста согласия; меняется **только вместе** с текстом на фронтенде |
| `RateLimit__InvitationTokenPer10Minutes` | нет, 30 | Лимит ручек по токену приглашения на IP |
| `TwoFactor__LockoutThreshold` | нет, 10 | Неверных кодов второго фактора подряд (по аккаунту) до блокировки |
| `TwoFactor__LockoutMinutes` | нет, 15 | На сколько минут блокируется второй фактор |

Секретов в `appsettings*.json` нет и не добавляем — только `.env`.

### Почта: SPF / DKIM / DMARC (проверено 2026-09-28)

| Запись | Значение | Состояние |
|---|---|---|
| SPF `genesis-hq.com` | `v=spf1 include:_spf.mx.cloudflare.net ~all` | Разрешает только Cloudflare Email Routing (входящая пересылка) |
| SPF `send.genesis-hq.com` (MAIL FROM Resend) | `v=spf1 include:amazonses.com ~all` | ✅ для писем через Resend |
| DKIM `resend._domainkey` | RSA-ключ Resend | ✅ |
| DKIM `cf2024-1._domainkey` | RSA-ключ Cloudflare | ✅ |
| DMARC `_dmarc.genesis-hq.com` | `v=DMARC1; p=none; rua=mailto:andrey@genesis-hq.com` | ⚠️ только мониторинг |

Что сделать и проверить:

1. **Каким SMTP уходит почта приложения** (`SMTP_HOST` в `.env`). Коды входа, письмо о принятии
   приглашения и уведомления идут через SMTP (`IEmailSender`), форма обратной связи — через Resend API.
   - `smtp.resend.com` — выравнивание уже есть (DKIM `resend`, SPF `send.`), ничего не нужно.
   - Любой другой (например, Gmail с паролем приложения) — письма с `From: no-reply@genesis-hq.com`
     не проходят SPF корня и не подписаны DKIM домена ⇒ DMARC fail. Либо перевести SMTP на Resend,
     либо добавить отправителя в SPF корня (`include:` провайдера) и настроить его DKIM.
2. Отправить себе код входа и письмо о принятии приглашения, в заголовках письма проверить
   `spf=pass`, `dkim=pass header.d=genesis-hq.com`, `dmarc=pass`.
3. Через 1–2 недели отчётов `rua` без посторонних отправителей — ужесточить DMARC:
   `v=DMARC1; p=quarantine; pct=100; rua=mailto:andrey@genesis-hq.com`, позже `p=reject`.
   С `p=none` поддельные письма «от площадки» доходят до получателей — а ссылка приглашения как раз
   то, что будут подделывать.

## 1. Бэкап БД

```bash
./scripts/backup.sh
```

Убедиться, что свежая копия появилась и проходит `gzip -t` (скрипт делает это сам; при ошибке — стоп).

## 2. Миграции

```bash
./scripts/migrate.sh
```

Приложение миграции само не накатывает. `migrate.sh` применяет все три по порядку под владельцем схемы.

## 3. Права роли приложения на новые таблицы

```bash
source scripts/load-env.sh; load_env .env
docker exec -i genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -v app_user="$POSTGRES_APP_USER" -v app_password="$POSTGRES_APP_PASSWORD" \
  -f /sql/create-app-role.sql
```

`.env` читается парсером `load-env.sh`, а не `set -a; . ./.env`: `SMTP_FROM_NAME` с пробелами
роняет `source`. Скрипт идемпотентен.

## 4. Бэкенд

```bash
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --build api
```

Без `-f docker-compose.proxy.yml` — публикация через Cloudflare Tunnel.

## 5. Первый владелец площадки и 2FA

Служебные роли теперь в `project_role_assignments`, миграция перенесла существующие. Если владельца
ещё нет:

```bash
docker exec -it genesis-api dotnet GenesisMarket.Api.dll grant-role <email> Owner --reason "Первый владелец"
```

Каждый сотрудник при следующем входе настраивает 2FA — без неё служебные разделы отвечают 403
`mfa_setup_required`.

## 6. Проверка

```bash
./scripts/smoke-deploy.sh
BASE_URL=https://api.genesis-hq.com ./scripts/smoke-deploy.sh
docker compose logs --tail=100 api | grep -iE 'error|fatal|Migration|Quartz'
systemctl status cloudflared

# приглашения: несуществующий токен — 404, служебная ручка анониму — 401, robots закрывает /invite/
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8090/api/invitations/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8090/api/admin/invitations
curl -s http://127.0.0.1:8090/robots.txt | grep invite

# джоб очистки приглашений зарегистрирован
docker compose logs api | grep -i invitation-cleanup
```

Ожидается: `404`, `401`, строка `Disallow: /invite/`.

Доступ после аудита ролей: модератор без взятой жалобы получает на `GET /api/moderation/users/{id}`
ответ 403 — это ожидаемо, контакты ему открываются только в карточке жалобы, которую он взял в работу.
У модератора больше нет разделов «Пользователи», «Бизнес», «Журнал» и «Карты» — теперь это уровень
Admin. Модератору остаются очередь, жалобы, скрытие отзывов и объявлений, предупреждения и счётчики.
Миграций это не требует: только код бэкенда и фронтенда.
Если сотрудник пишет «вход заблокирован на 15 минут» — сработала блокировка второго фактора
(`2fa.locked` в журнале действий); досрочно снять можно `reset-2fa` (сбросит и саму 2FA).

## 7. Фронтенд (последним)

Деплой проекта в Vercel (push в ветку прод-деплоя или `vercel --prod`). Только после того, как шаг 6
прошёл: старый фронтенд с новым API работает, новый фронтенд со старым API — нет.

## Откат

```bash
git checkout <прошлый_коммит>
./scripts/migrate.sh <ID_миграции_из_шага_0>     # схема назад
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --build api
```

Откат `SellerInvitations` удаляет таблицы приглашений **вместе с согласиями на публикацию** — перед
ним сохранить `publication_consents` отдельно (`pg_dump -t publication_consents`). Откат
`StaffRolesAuditTwoFactor` возвращает роль в `users.Role`, но журнал действий теряется — поэтому
при проблемах сначала откатывать только код, а схему — лишь если новая схема ломает старый код.
Если откатывать нечем — восстановление из бэкапа шага 1.
