# Выкатка магазинных аккаунтов по приглашению

Эта памятка относится к серверному обновлению с миграцией
`20261026214831_InvitedStoreAccounts`. Она предназначена для запуска на production Linux-сервере
из SSH-сессии. Команды здесь подготовлены; они не запускались на production.

Миграция расширяет существующую таблицу `business_profiles`, добавляет признак магазина и город
витрины, а также переносит аккаунты с принятыми приглашениями. Новых таблиц нет, поэтому выдавать
роли приложения дополнительные права на новые таблицы не требуется.

## Перед выкладкой

Сначала включить release-коммит в `origin/master`. На production-сервере:

```bash
cd /opt/genesis/SERVER
git status --short --branch
git fetch origin
git pull --ff-only origin master
git log -1 --oneline

# Сверить последнюю миграцию перед изменением схемы.
source scripts/load-env.sh
load_env .env
docker exec genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1;'
```

Убедиться, что рабочая копия чистая, обновление fast-forward применилось к ожидаемому коммиту,
а миграция ещё не применена. Если проверка показывает неожиданный коммит или состояние Git —
остановиться и выяснить причину до следующих шагов.

## Резервная копия, миграция и API

```bash
# Снимок PostgreSQL и объектов MinIO. Проверить, что скрипт завершился успешно.
./scripts/backup.sh

# Применить миграцию под владельцем схемы.
./scripts/migrate.sh

# Убедиться, что нужная миграция стала последней.
source scripts/load-env.sh
load_env .env
docker exec genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1;'

# Собрать новый production-образ и обновить только API.
I_CONFIRM_PROD_LAUNCH=yes docker compose up -d --build api
```

В выводе запроса после миграции ожидается `20261026214831_InvitedStoreAccounts`.
Не запускать `docker compose down -v`: эта команда удалит production volumes.

## Проверка выкладки

```bash
# Дождаться готовности API и его зависимостей (порт по умолчанию — 8090).
ready=0
for i in $(seq 1 60); do
  if curl -fsS --max-time 3 http://127.0.0.1:8090/health/ready; then
    ready=1
    break
  fi
  sleep 2
done
test "$ready" -eq 1

# Полный локальный smoke-тест production-стека.
./scripts/smoke-deploy.sh

# Проверить внешний адрес, доступный пользователям.
BASE_URL=https://api.genesis-hq.com ./scripts/smoke-deploy.sh

# Compose-файл требует подтверждение на каждой production-команде.
I_CONFIRM_PROD_LAUNCH=yes docker compose ps
I_CONFIRM_PROD_LAUNCH=yes docker compose logs --tail=100 api

# Убедиться, что последняя миграция применена.
source scripts/load-env.sh
load_env .env
docker exec genesis-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1;'
```

После проверки новый backend совместим со старым frontend. Новый frontend выкладывать отдельно,
после проверки его собственного release.

## Если API не проходит проверку

Не запускать обычный откат кода или `./scripts/migrate.sh <предыдущая-миграция>` автоматически.
После успешной миграции в БД могут быть магазинные профили с пустыми юридическими полями, а откат
схемы снова делает эти колонки обязательными. В этом случае `Down` миграции не сможет безопасно
примениться. Оставить миграцию на месте, сохранить логи и выполнить fix-forward либо согласовать
восстановление резервной копии с учётом записей, появившихся после её создания.
