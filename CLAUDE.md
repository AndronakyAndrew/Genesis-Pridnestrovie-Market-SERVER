# Genesis Market — фронтенд

## Продукт
Доска объявлений C2C для Приднестровья. Бэкенд: ASP.NET Core Web API,
спецификация в `pmr_market_prompt.md`. Сделки офлайн: платежей и доставки нет.

## Стек
Next.js 16 (App Router) + React 19 + TypeScript, Tailwind CSS 4.
Данные — TanStack Query. Формы — React Hook Form + Zod.
Тесты — Vitest + Testing Library + Playwright.
Server Components по умолчанию; `"use client"` — только там, где нужны
хуки/события.

## Незыблемые правила
- Ни одного хардкоженного цвета, отступа, радиуса, размера шрифта
  в компонентах. Только токены из `globals.css` / конфига Tailwind.
- Города, категории и подкатегории приходят с сервера
  (`GET /api/capabilities`). В клиенте не дублируются никогда.
- Доступность функций определяется флагами из `/api/capabilities`.
  Отключённые показывают «Функция будет доступна в следующем обновлении
  сервера» через общий компонент `<FeatureGate>`, не хардкодом.
- Токены доступа не хранятся в localStorage. Access token — в памяти,
  refresh — в httpOnly cookie либо в защищённом хранилище по решению шага F1.
- Никакой бизнес-логики в компонентах: правила живут в хуках и сервисах.
- Все тексты интерфейса — русский, вынесены в `src/i18n/ru.ts`,
  в компонентах не хардкодятся.
- Валюта: `15 000 руб.` — рубль ПМР, неразрывный пробел в разрядах.
  Цены с сервера приходят в копейках (bigint), форматируются одной
  общей функцией.
- Даты: `28 июня 2026`; до 48 часов — относительные («2 часа назад»).
  Часовой пояс отображения Europe/Chisinau, с сервера приходит UTC.
- Mobile-first. Мобильный layout — отдельное решение, не сжатый десктоп.
- Каждый интерактивный элемент имеет состояния: default, hover,
  focus-visible, active, disabled, loading.
- Каждый экран имеет состояния: загрузка (skeleton, не спиннер),
  пусто, ошибка, нет прав.
- Никаких `any` в типах. Типы API генерируются из OpenAPI, руками не пишутся.

## Токены
Источник истины — официальный бренд-гайд Genesis
(`Genesis Industries Corp (Assets)/Genesis Brand.html`),
значения зафиксированы в `src/app/globals.css` (`:root`).

Тёмная тема: фон `#0A1315`, поверхность `#0F1B1E`,
вложенные блоки `#16262A`, акцент `#14E8C4` (Genesis Teal — кнопки,
ссылки, иконки, цены), светлый teal `#5FF0D8` (hover/градиент),
текст `#FFFFFF`, приглушённый `#9AA9AC`, граница `#223231`.
Радиусы: карточки 8px, кнопки 6px.

Шрифты: `Manrope` (заголовки/текст, `--font-sans`) +
`IBM Plex Mono` (моно-подписи, `--font-mono`).

Акцентная роль единая: `--primary` и `--accent` — один и тот же teal;
структуру двух токенов сохраняем, чтобы развести их позже при нужде.

Логотип — компонент `src/components/Logo.tsx` (знак «G» + вордмарк,
«Market» акцентом, опциональная моно-подпись).

## Структура
```
src/
  app/            — App Router (страницы, layout, globals.css с токенами)
  components/     — переиспользуемые компоненты (Logo, FeatureGate и др.)
  hooks/          — бизнес-правила и доступ к данным
  lib/api/        — клиент API (единственная точка обращения к backend)
  i18n/ru.ts      — все тексты интерфейса
```

## API / backend
Backend — ASP.NET Core (проект-сиблинг `SERVER/`).

Боевые адреса (свой домен, с 2026-09-07):
- API — `https://api.genesis-hq.com` (Cloudflare Tunnel с серверного ноутбука).
- Фронтенд — `https://market.genesis-hq.com` (Vercel, свой домен).

Адрес API задаётся `NEXT_PUBLIC_API_BASE_URL`: локально — `.env.local`,
на проде — переменная проекта в Vercel. То же значение продублировано
дефолтом в четырёх файлах: `src/lib/api/http.ts`, `next.config.ts` и два
route-handler'а (`/api/img`, `/api/session`). При смене адреса правятся все
четыре плюс переменная окружения.

Все запросы — через `src/lib/api` (`import { api } from "@/lib/api"`);
`fetch` в компонентах не вызывать. Слой:
- `types.ts` — типы, повторяют C#-DTO из `SERVER/.../Contracts`.
- `http.ts` — `apiFetch`, `ApiError` (разбирает ProblemDetails),
  `tokenStore` (access-токен только в памяти).
- `endpoints.ts` — функции всех ручек, сгруппированы по фичам.

Контракт с сервером (проверено на живом API):
- Свойства JSON — **camelCase**; **enum'ы — PascalCase**
  (`"Tiraspol"`, `"Fixed"`, `"Active"`). Биндинг enum на входе
  регистронезависим, но в ответах — PascalCase.
- Ошибки — `application/problem+json`, `title` на русском → `ApiError.title`.
- Access + refresh токены приходят в теле `AuthResponse` (сервер cookie
  не ставит). Access — в память (`tokenStore`), refresh — по решению F1;
  в localStorage не класть.
- Заголовков-обходов интерстициала (`ngrok-skip-browser-warning`) больше нет:
  Cloudflare отдаёт ответ API напрямую. Не возвращать.
- Auth — свой JWT (`Authorization: Bearer`), не cookie ⇒ на сервере
  origin фронта должен быть в `Cors:AllowedOrigins` (иначе CORS-блок).

## Правила, добавленные после найденных проблем
<!-- Каждый найденный баг → строка сюда, чтобы он не повторился -->
- **Нет `GET /api/capabilities`.** Справочники с сервера получить негде:
  города/категории — фиксированные enum'ы, эндпоинта подкатегорий тоже нет.
  Правило «справочники приходят с сервера» пока не выполнимо — города/
  категории берём из типов, подкатегории (int FK) нужны с сервера, но их
  ручки нет. Не выдумывать значения; при появлении эндпоинта — подключить.
- **Цена — `decimal`, не «копейки/bigint».** `ListingResponse.price` —
  decimal рублей (JSON number); фильтр `priceFrom/priceTo` — целые (`long`).
  Форматтер цены должен исходить из рублей-decimal, а не копеек.
- **Типы API написаны руками** (Swagger закрыт вне Development, 401).
  Правило «генерировать из OpenAPI» отложено до доступа к `/swagger`.

<!-- BEGIN:nextjs-agent-rules -->

## Правила инфраструктуры и безопасности сервера

Закреплено после прогона 2026-09-05 (`docs/server-security-log.md`).
Каждая строка — следствие конкретной находки.

- Секретов нет ни в одном `appsettings*.json`, включая `appsettings.Development.json`:
  локальные креды — в user-secrets или `.env.dev`. Проверяется на старте и в CI.
- Наружу (`0.0.0.0`) не смотрит ни один порт. API — на `127.0.0.1`,
  PostgreSQL и MinIO — без `ports` вообще. Публикация — Cloudflare Tunnel:
  `cloudflared` на хосте отдаёт `api.genesis-hq.com` в `127.0.0.1:8090`
  (`docs/cloudflare-tunnel.md`). Caddy-overlay `docker-compose.proxy.yml` —
  альтернатива туннелю на случай белого IP, вместе они не поднимаются.
- В прод-`CORS_ALLOWED_ORIGINS` только боевые origin фронтенда. `*` и
  `http://localhost:*` — нет: прод-периметр не включает машины разработки.
- Реальный IP клиента приходит в `X-Forwarded-For` от edge Cloudflare;
  доверие к заголовку — только из `TRUSTED_PROXY_NETWORKS`. Менять периметр
  публикации, не проверив, что лимиты по IP считаются по посетителю,
  а не по туннелю, нельзя: иначе один глобальный лимит кладёт сайт целиком.
- Adminer и любой другой веб-клиент БД — только в `docker-compose.dev.yml`.
- `bin/`, `obj/`, `backups/`, `db-backups/` в git не попадают.
- `.dockerignore` исключает `**/appsettings.*.json`; в образе только базовый `appsettings.json`.
- `ASPNETCORE_ENVIRONMENT` задаётся явно; неизвестное или пустое значение роняет старт.
- Заглушки, пишущие в лог вместо отправки (SMS/SMTP/Resend), регистрируются
  только в Development; вне его отсутствие конфигурации — ошибка старта.
- Любое загруженное изображение проходит через `IImageProcessor` до записи
  в хранилище: тип по magic bytes, снятие EXIF/IPTC/XMP, перекодирование.
- Приложение ходит в БД под ролью с правами только на DML (`POSTGRES_APP_USER`).
  DDL — отдельный шаг деплоя `scripts/migrate.sh` под владельцем схемы.
- Миграции не накатываются при старте приложения.
- Образы закреплены по версии или digest; `:latest` не используется.
- У каждого сервиса в compose есть `logging` с ротацией и лимиты `mem_limit`/`cpus`/`pids_limit`.
- Публичные эндпоинты не раскрывают состав стека: `/health/ready` отдаёт только сводный статус.
- Заголовки безопасности и отсутствие `Server` проверяются `scripts/smoke-deploy.sh`
  на реально поднятом стеке, а не по конфигурации.
- Обслуживающие скрипты обращаются к контейнерам через `docker exec` по имени,
  не через `docker compose exec` (guard прод-файла ломает cron).
- Уязвимые пакеты и секреты в истории роняют CI, а не предупреждают.
- Actions в workflow закреплены по SHA; `pull_request_target` не используется.

# This is NOT the Next.js you know

This version has breaking changes — APIs, conventions, and file structure may all differ from your training data. Read the relevant guide in `node_modules/next/dist/docs/` (resolved from this file's directory; in monorepos the `next` package may not be visible from the repo root) before writing any code. Heed deprecation notices.

This block is written and re-added by `next dev` — verify at `node_modules/next/dist/server/lib/generate-agent-files.js`. Removing it from a diff only re-creates the uncommitted change; committing it with your work keeps the tree clean.

<!-- END:nextjs-agent-rules -->
