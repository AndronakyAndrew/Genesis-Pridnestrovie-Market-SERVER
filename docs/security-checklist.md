# Genesis Market — чеклист безопасности перед релизом

Минимальная версия: **Проходы 1, 3, 4, 8** шаблона поиска уязвимостей. Проходить
целиком перед каждым публичным деплоем. Отмечать `[x]` только после ручной проверки —
галочка «по памяти» не считается. Рядом с каждым пунктом — где живёт контроль в коде.

Быстрые проверки перед началом:

```bash
dotnet build            # без предупреждений (TreatWarningsAsErrors)
dotnet test             # весь набор зелёный
./scripts/check-image-secrets.sh     # секретов в слоях образа нет
./scripts/restore-check.sh           # последний бэкап восстанавливается
```

---

## Проход 1 — Аутентификация, сессии, токены

- [ ] JWT: проверяются issuer, audience, lifetime, подпись HS256; `ClockSkew=0`;
      алгоритм зафиксирован (`ValidAlgorithms=[HmacSha256]`). — `Auth/AuthServiceCollectionExtensions.cs`
- [ ] Ключ подписи только из env, ≥32 байт; без него приложение не стартует. — там же + `Configuration/OptionsValidationSetup.cs`
- [ ] Пароли: BCrypt, длина 8–72 байта, отбраковка частых паролей, rehash при смене workFactor. — `Infrastructure/Auth`, `Controllers/AuthController.cs`
- [ ] Логин: единый ответ на неверный email/пароль (анти-перечисление), проверка dummy-хеша по времени. — `AuthController.Login`
- [ ] Refresh-токены: ротация, обнаружение повторного использования → отзыв всей цепочки. — `Auth/RefreshTokenService.cs`, `AuthTests`
- [ ] Смена пароля/бан: немедленная инвалидация выданных access-токенов через SecurityStamp. — `Auth/SecurityStampValidator.cs`
- [ ] Rate-limit входа: 5/15 мин на (IP, email) в экшене; register 3/час, глобально 300/мин. — `Auth/AuthRateLimiter.cs`, `Security/RateLimitingSetup.cs`
- [ ] Модель запроса логина не логируется целиком; секреты маскируются. — `Security/MaskingDestructuringPolicy.cs`, `SecurityMaskingTests`

## Проход 3 — Авторизация и контроль доступа

- [ ] `FallbackPolicy` требует аутентификации: забытый `[Authorize]` не открывает эндпоинт; публичные помечены `[AllowAnonymous]`. — `AuthServiceCollectionExtensions.cs`
- [ ] Владение ресурсом проверяется на сервере (не скрытием кнопок): `ResourceOwner` + явные `OwnerId`-проверки. — `Auth/ResourceOwnerAuthorization.cs`, контроллеры
- [ ] Роли берутся только из claim токена; нет хардкод-списков админов. — `Auth/CurrentUser.cs`, `Controllers/ModerationController.cs`
- [ ] IDOR: доступ к чужому ресурсу → 403 и запись `resource.forbidden` в журнал безопасности. — `ResourceOwnerHandler`, `Security/SecurityAudit.cs`
- [ ] Действия модератора (approve/reject/ban/unban/просмотр контактов) пишутся в `moderation_logs` и в журнал безопасности. — `Moderation/ModerationAudit.cs`
- [ ] Эскалация ролей/само-бан заблокированы (нельзя забанить себя/админа). — `ModerationController.Ban`
- [ ] Проверить матрицу доступа. — `docs/authorization-matrix.md`, `AuthorizationTests`

## Проход 4 — Ввод и инъекции

- [ ] Все запросы к БД — через EF Core (параметризация); сырого SQL с конкатенацией нет. — `Infrastructure/Persistence`, контроллеры
- [ ] DTO валидируются (DataAnnotations / FluentValidation); ошибки — `Problem(...)` без утечки деталей. — `Contracts/`, `Listings/ListingValidators.cs`
- [ ] Enum в JSON строками; неизвестные значения не проваливаются в БД. — `Program.cs` (JsonStringEnumConverter)
- [ ] Загрузка фото: тип/размер/лимиты, ключи объектов не задаются клиентом. — `Controllers/ListingImagesController.cs`, `Infrastructure/Imaging`
- [ ] Ошибки не раскрывают стектрейсы/SQL/имена констрейнтов в Production (только traceId). — `Middleware/GlobalExceptionHandler.cs`
- [ ] Курсоры/поисковые строки не доверяются: декод с валидацией, whitelisting сортировок. — `Listings/CatalogCursor.cs`, `Moderation/ModerationCursor.cs`

## Проход 8 — Конфигурация, секреты, инфраструктура

- [ ] Секреты только из env: в `appsettings.json` пусто; старт падает при нарушении. — `Configuration/OptionsValidationSetup.cs` (AppSettingsSecretScanner)
- [ ] Production не поднимается с дефолтным паролем БД / пустым CORS / без `Security:IpHashKey`. — там же
- [ ] Заголовки безопасности на всех ответах: `nosniff`, `DENY`, `no-referrer`, HSTS за TLS, CSP. — `Security/SecurityHeadersMiddleware.cs`
- [ ] CORS — явный белый список origin; `AllowAnyOrigin` + `AllowCredentials` не используются. — `Program.cs`
- [ ] За прокси доверяем только заданным сетям/адресам (ForwardedHeaders), IP клиента корректен. — `Security/NetworkSetup.cs`
- [ ] Swagger только в Development; в Production не регистрируется. — `Program.cs`
- [ ] Docker: non-root, read-only ФС, `no-new-privileges`, `cap_drop: ALL`, лимиты памяти/CPU/pids. — `Dockerfile`, `docker-compose.yml`
- [ ] Финальный образ на runtime-deps (не sdk); секретов в слоях нет. — `Dockerfile`, `./scripts/check-image-secrets.sh`
- [ ] Логи: Serilog JSON, correlation id в каждом запросе, отдельный журнал событий безопасности, маскирование. — `Program.cs`, `Middleware/RequestIdEnricherMiddleware.cs`, `Security/SecurityAudit.cs`
- [ ] OpenTelemetry: трейсы HTTP+EF Core, метрики; экспорт OTLP по `OTEL_EXPORTER_OTLP_ENDPOINT`. — `Observability/ObservabilitySetup.cs`
- [ ] Резервные копии по расписанию + **проверенное** восстановление. — `scripts/backup.sh`, `scripts/restore-check.sh`
