-- ============================================================================
--  Genesis Market — перевод тестовых объявлений модератора на флаг IsExample.
--
--  Разовый ручной скрипт для прода. Что делает с объявлениями аккаунтов
--  с ролью Moderator/Admin, чей заголовок начинается с «[ ТЕСТ ]» или «[ТЕСТ]»:
--    • IsExample = true;
--    • убирает префикс из заголовка (+ trim);
--    • убирает «⚠️ Тестовое объявление.» и следующие пробелы/переносы из начала описания;
--    • снимает «продвижение» (см. шаг 2.4);
--    • Slug НЕ трогает.
--
--  Схема сверена с EF-моделью и миграциями (2026-09-17):
--    listings: "Id", "Title", "Description", "Slug", "Status" (native enum
--      listing_status, метки в нижнем регистре: 'active', 'pendingreview', …),
--      "PublishedAt", "ApprovedAt", "BumpedAt", "CreatedAt", "UpdatedAt",
--      "DeletedAt", "OwnerId", "IsExample";
--    users: "Id", "Email", "Role" (строка: 'User' | 'Moderator' | 'Admin').
--
--  Предусловие: применена миграция 20260917113359_AddListingIsExample.
--  Без неё скрипт остановится на первом же запросе (нет колонки "IsExample").
--
--  Запуск (на сервере, из каталога стека). Интерактивно — чтобы после проверки
--  самому набрать COMMIT или ROLLBACK:
--    docker cp scripts/mark-example-listings.sql genesis-postgres:/tmp/mark-example-listings.sql
--    docker exec -it genesis-postgres psql -U genesis -d genesis
--    genesis=# \i /tmp/mark-example-listings.sql
--    ... смотрим вывод ...
--    genesis=*# COMMIT;      -- или ROLLBACK;
--
--  ВАЖНО: пока транзакция открыта, строки этих объявлений заблокированы.
--  Открытие их карточки на сайте (UPDATE счётчика просмотров) будет ждать
--  COMMIT/ROLLBACK. Решение принимать без долгих пауз.
--
--  При ошибке внутри транзакции psql остановит скрипт (ON_ERROR_STOP),
--  транзакция будет в состоянии aborted — набрать ROLLBACK.
-- ============================================================================

\set ON_ERROR_STOP on
SET client_encoding = 'UTF8';

-- Срок автоархивации, дней: CatalogHygiene:ArchiveAfterDays (appsettings.json = 30).
-- Если на проде переопределён через CatalogHygiene__ArchiveAfterDays — поправить здесь.
\set archive_after_days 30

-- ---------------------------------------------------------------------------
-- 0. Набор целей — один раз, чтобы превью, UPDATE и проверка работали по одним
--    и тем же Id (после правки заголовки уже не начинаются с префикса).
-- ---------------------------------------------------------------------------
DROP TABLE IF EXISTS pg_temp.example_targets;

CREATE TEMP TABLE example_targets AS
SELECT l."Id"
FROM listings l
JOIN users u ON u."Id" = l."OwnerId"
WHERE u."Role" IN ('Moderator', 'Admin')
  -- Точнее — закрепить конкретный аккаунт вместо роли:
  -- AND u."Email" = 'moderator@example.com'
  AND (l."Title" LIKE '[ ТЕСТ ]%' OR l."Title" LIKE '[ТЕСТ]%')
  AND l."IsExample" = false;

-- ---------------------------------------------------------------------------
-- 1. ПРЕВЬЮ: что будет затронуто и как изменится. Проверить перед шагом 2:
--    • new_title_len в диапазоне 5..120 (иначе упадёт ck_listings_title_length);
--    • desc_prefix_found — где описание начинается с пометки;
--    • archived_on_next_run = true — объявление уйдёт в архив ближайшим
--      ночным прогоном гигиены (03:00) после снятия продвижения (см. 2.4).
-- ---------------------------------------------------------------------------
SELECT
    l."Id",
    l."Slug",
    l."Status",
    l."DeletedAt" IS NOT NULL                                            AS is_deleted,
    u."Email"                                                            AS owner_email,
    u."Role"                                                             AS owner_role,
    l."Title"                                                            AS old_title,
    btrim(regexp_replace(l."Title", '^(\[ ТЕСТ \]|\[ТЕСТ\])', ''), E' \t\r\n') AS new_title,
    char_length(btrim(regexp_replace(l."Title", '^(\[ ТЕСТ \]|\[ТЕСТ\])', ''), E' \t\r\n')) AS new_title_len,
    l."Description" ~ '^\s*⚠️?\s*Тестовое объявление\.'       AS desc_prefix_found,
    left(regexp_replace(l."Description", '^\s*⚠️?\s*Тестовое объявление\.\s*', ''), 60) AS new_description_head,
    -- «Продвинуто» сейчас — та же формула, что CatalogRow.ToCard (IsBumped).
    (l."PublishedAt" IS NOT NULL
        AND l."BumpedAt" > GREATEST(l."PublishedAt", l."ApprovedAt"))    AS is_bumped_now,
    l."BumpedAt"                                                         AS old_bumped_at,
    GREATEST(l."PublishedAt", l."ApprovedAt")                            AS new_bumped_at_if_bumped,
    l."Status" = 'active'
      AND COALESCE(
            CASE WHEN l."PublishedAt" IS NOT NULL
                      AND l."BumpedAt" > GREATEST(l."PublishedAt", l."ApprovedAt")
                 THEN GREATEST(l."PublishedAt", l."ApprovedAt")
                 ELSE l."BumpedAt" END,
            l."PublishedAt", l."CreatedAt")
          < now() - make_interval(days => :archive_after_days)         AS archived_on_next_run
FROM example_targets t
JOIN listings l ON l."Id" = t."Id"
JOIN users u ON u."Id" = l."OwnerId"
ORDER BY u."Email", l."CreatedAt";

SELECT count(*) AS targets_total FROM example_targets;

-- ---------------------------------------------------------------------------
-- 2. ИЗМЕНЕНИЯ — в транзакции.
-- ---------------------------------------------------------------------------
BEGIN;

-- Не висеть на блокировке, если строку прямо сейчас держит API.
SET LOCAL lock_timeout = '5s';

UPDATE listings l
SET
    -- 2.1 Флаг примера.
    "IsExample" = true,

    -- 2.2 Префикс заголовка: ровно два известных варианта, затем trim.
    --     SearchVector — generated-колонка, пересчитается сама. Slug не трогаем.
    "Title" = btrim(regexp_replace(l."Title", '^(\[ ТЕСТ \]|\[ТЕСТ\])', ''), E' \t\r\n'),

    -- 2.3 Пометка в начале описания вместе со следующими пробелами и переносами.
    --     ⚠ — «⚠», ️? — необязательный селектор эмодзи-представления.
    --     Нет пометки — описание не меняется.
    "Description" = regexp_replace(l."Description", '^\s*⚠️?\s*Тестовое объявление\.\s*', ''),

    -- 2.4 «Продвижение». Платных услуг в коде нет (см. ListingsController.Bump:
    --     «платных услуг в MVP нет»). Бейдж «Продвинуто» — вычисляемый IsBumped:
    --     BumpedAt позже и PublishedAt, и ApprovedAt. Снимаем его, возвращая
    --     BumpedAt к моменту попадания в каталог — GREATEST(PublishedAt, ApprovedAt),
    --     как после одобрения без поднятия. Непродвинутые не трогаем.
    --     Побочный эффект: BumpedAt — ещё и ключ сортировки по умолчанию и точка
    --     отсчёта автоархивации. См. archived_on_next_run в превью.
    "BumpedAt" = CASE
        WHEN l."PublishedAt" IS NOT NULL
             AND l."BumpedAt" > GREATEST(l."PublishedAt", l."ApprovedAt")
        THEN GREATEST(l."PublishedAt", l."ApprovedAt")
        ELSE l."BumpedAt"
    END,

    "UpdatedAt" = now()
FROM example_targets t
WHERE l."Id" = t."Id"
  -- Повторная защита: строка не изменилась между превью и UPDATE.
  AND l."IsExample" = false
  AND (l."Title" LIKE '[ ТЕСТ ]%' OR l."Title" LIKE '[ТЕСТ]%');

-- ---------------------------------------------------------------------------
-- 3. ПРОВЕРКА результата (внутри транзакции, до COMMIT).
-- ---------------------------------------------------------------------------
SELECT
    l."Id",
    l."Slug",
    l."Status",
    l."IsExample",
    l."Title",
    left(l."Description", 60)                                            AS description_head,
    l."Description" ~ '^\s*⚠️?\s*Тестовое объявление\.'       AS desc_prefix_left,
    (l."PublishedAt" IS NOT NULL
        AND l."BumpedAt" > GREATEST(l."PublishedAt", l."ApprovedAt"))    AS is_bumped,
    l."BumpedAt",
    l."UpdatedAt"
FROM example_targets t
JOIN listings l ON l."Id" = t."Id"
ORDER BY l."CreatedAt";

-- Сводка: все цели помечены, префиксов и пометок не осталось, ничего не «продвинуто».
-- Ожидается: marked = targets_total, остальные счётчики = 0.
SELECT
    (SELECT count(*) FROM example_targets)                               AS targets_total,
    count(*) FILTER (WHERE l."IsExample")                                AS marked,
    count(*) FILTER (WHERE l."Title" LIKE '[ ТЕСТ ]%' OR l."Title" LIKE '[ТЕСТ]%') AS title_prefix_left,
    count(*) FILTER (WHERE l."Description" ~ '^\s*⚠️?\s*Тестовое объявление\.') AS desc_prefix_left,
    count(*) FILTER (WHERE l."PublishedAt" IS NOT NULL
                       AND l."BumpedAt" > GREATEST(l."PublishedAt", l."ApprovedAt")) AS still_bumped
FROM example_targets t
JOIN listings l ON l."Id" = t."Id";

-- Остались ли у модераторов тестовые заголовки вне набора (например, уже с IsExample).
SELECT l."Id", l."Title", l."IsExample"
FROM listings l
JOIN users u ON u."Id" = l."OwnerId"
WHERE u."Role" IN ('Moderator', 'Admin')
  AND (l."Title" LIKE '[ ТЕСТ ]%' OR l."Title" LIKE '[ТЕСТ]%');

-- ---------------------------------------------------------------------------
-- 4. Подтверждение вручную. Всё сходится — COMMIT; иначе — ROLLBACK.
-- ---------------------------------------------------------------------------
-- COMMIT;
-- ROLLBACK;
