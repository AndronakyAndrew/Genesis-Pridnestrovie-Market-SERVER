-- ============================================================================
--  Genesis Market — ограниченная роль приложения для PostgreSQL.
--
--  Зачем: образ postgres создаёт POSTGRES_USER суперпользователем, и API ходит
--  в базу под ним. Утечка строки подключения или SQL-инъекция тогда означают
--  полный контроль над кластером. Приложению нужны только SELECT/INSERT/UPDATE/
--  DELETE — DDL выполняет отдельный шаг деплоя (миграции EF Core под
--  POSTGRES_USER, см. scripts/migrate.sh).
--
--  Идемпотентен: прогоняй повторно после каждой миграции, чтобы выдать права
--  на добавившиеся таблицы (default privileges покрывают только новые объекты,
--  созданные ПОСЛЕ первого запуска этого скрипта).
--
--  Запуск (роль и пароль передаются переменными, в файле их нет):
--    docker exec -i genesis-postgres \
--      psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--           -v app_user=genesis_app -v app_password='<длинный случайный пароль>' \
--           -f /sql/create-app-role.sql
--
--  Пароль сгенерировать: openssl rand -hex 24
--
--  После этого впиши в .env:
--    POSTGRES_APP_USER=genesis_app
--    POSTGRES_APP_PASSWORD=<тот же пароль>
--  и перезапусти api:
--    I_CONFIRM_PROD_LAUNCH=yes docker compose up -d api
--
--  Прим.: psql НЕ подставляет переменные внутри dollar-quoted блоков ($$...$$),
--  поэтому создание роли сделано через \gexec, а не через DO.
-- ============================================================================

\set ON_ERROR_STOP on

-- 1. Роль, если её ещё нет. Только LOGIN: ни SUPERUSER, ни CREATEDB, ни CREATEROLE.
SELECT format('CREATE ROLE %I LOGIN', :'app_user')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_user')
\gexec

-- 2. Пароль и явное снятие всех административных атрибутов (в т.ч. если роль
--    уже существовала и была заведена с лишними правами).
ALTER ROLE :"app_user" WITH LOGIN PASSWORD :'app_password'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- 3. Подключение к текущей базе и чтение схемы. CREATE на схеме не выдаём:
--    создавать объекты приложение не должно.
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'app_user')
\gexec

GRANT USAGE ON SCHEMA public TO :"app_user";
REVOKE CREATE ON SCHEMA public FROM :"app_user";

-- 4. Данные: только DML на существующие таблицы (включая служебные qrtz_* Quartz).
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"app_user";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"app_user";

-- 5. Те же права на объекты, которые создадут будущие миграции. Default privileges
--    привязаны к роли-создателю — здесь это тот, кто гоняет миграции.
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app_user";
ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :"app_user";

-- 6. Контроль: все административные флаги должны быть false.
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls
FROM pg_roles
WHERE rolname = :'app_user';
