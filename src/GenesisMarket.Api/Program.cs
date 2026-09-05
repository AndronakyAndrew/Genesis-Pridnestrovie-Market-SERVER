using System.Text.Json.Serialization;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Configuration;
using GenesisMarket.Api.Feedback;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Middleware;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Api.Observability;
using GenesisMarket.Api.Outbox;
using GenesisMarket.Api.SavedSearches;
using GenesisMarket.Api.Security;
using GenesisMarket.Api.Seo;
using GenesisMarket.Api.Trust;
using GenesisMarket.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Formatting.Compact;

// Ранний логгер (bootstrap) — ловит ошибки ещё до сборки хоста.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---- Serilog: структурированный JSON в консоль, уровень из конфигурации ----
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        // Маскирование секретов при деструктуризации ({@obj}): password/token/phone/email → ***.
        .Destructure.With(new MaskingDestructuringPolicy())
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

    // ---- Kestrel: потолки запроса и таймауты ----
    // Без них работают дефолты (тело 30 МБ), а самый крупный легитимный запрос —
    // загрузка фото на 10 МБ. Значения настраиваются из окружения (секция Kestrel:Limits),
    // чтобы менять их без пересборки образа. AddServerHeader=false убирает «Server: Kestrel»:
    // сообщать сканерам, чем именно отвечает сервер, незачем.
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        var limits = builder.Configuration.GetSection("Kestrel:Limits");

        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = limits.GetValue("MaxRequestBodySizeBytes", 12L * 1024 * 1024);
        kestrel.Limits.MaxRequestHeadersTotalSize = limits.GetValue("MaxRequestHeadersTotalSizeBytes", 32 * 1024);
        kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(limits.GetValue("KeepAliveTimeoutSeconds", 120));
        kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(limits.GetValue("RequestHeadersTimeoutSeconds", 30));
    });

    // ---- CORS: origin-ы строго из конфигурации, без AllowAnyOrigin ----
    const string corsPolicy = "GenesisCors";
    // Список origin-ов приходит строкой через запятую (удобно для env).
    var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    builder.Services.AddCors(options =>
        options.AddPolicy(corsPolicy, policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

    // ---- Валидация конфигурации при старте: пустой JWT-ключ, дефолтный пароль БД,
    //      пустой CORS, секреты в appsettings — не дают приложению подняться ----
    builder.Services.AddGenesisOptionsValidation(builder.Configuration, builder.Environment);

    // ---- Доверие к заголовкам обратного прокси (Caddy): корректный IP и схема (HTTPS) ----
    builder.Services.AddGenesisForwardedHeaders(builder.Configuration);

    // ---- Единая политика rate-limit на встроенном RateLimiter (429 + Retry-After) ----
    builder.Services.AddGenesisRateLimiting(builder.Configuration);

    // ---- OpenTelemetry: трейсы (HTTP + EF Core) и метрики (экспорт OTLP по env) ----
    builder.Services.AddGenesisObservability(builder.Configuration);

    // ---- Журнал событий безопасности (вход, бан, доступ к чужому ресурсу, rate-limit) ----
    builder.Services.AddScoped<ISecurityAudit, SecurityAudit>();

    // ---- Инфраструктура: PostgreSQL + MinIO + их health checks ----
    builder.Services.AddInfrastructure(builder.Configuration);

    // ---- Собственная аутентификация: JWT + BCrypt (Identity не используем) ----
    builder.Services.AddGenesisAuth(builder.Configuration, builder.Environment);

    // ---- Жизненный цикл объявлений (пороги, премодерация, просмотры, валидаторы) ----
    builder.Services.AddListingsFeature(builder.Configuration);

    // ---- Слой доверия: отзывы (репутация) и жалобы (модерация) ----
    builder.Services.AddTrustFeature(builder.Configuration);

    // ---- Инструменты модератора: очередь, действия, аудит-журнал ----
    builder.Services.AddModerationFeature();

    // ---- Форма обратной связи: письма через Resend (уведомление на служебный адрес) ----
    builder.Services.AddFeedbackFeature(builder.Configuration, builder.Environment);

    // ---- Транзакционный outbox: доставка уведомлений (email/Telegram) и удаление объектов ----
    builder.Services.AddOutbox(builder.Configuration);

    // ---- Сохранённые поиски: рассылка новых совпадений (Quartz-джоб + сервис) ----
    builder.Services.AddSavedSearchesFeature();

    // ---- SEO: мета карточек, sitemap, robots, посадочные (органический трафик) ----
    builder.Services.AddSeoFeature(builder.Configuration);

    // ---- ProblemDetails + глобальный обработчик исключений ----
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // enum-ы в JSON — строками ("tiraspol", "fixed"), как ожидает клиент.
    builder.Services
        .AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // ---- Swagger: генерируем только вне Production ----
    if (!builder.Environment.IsProduction())
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
    }

    var app = builder.Build();

    // Порядок middleware важен.
    // ForwardedHeaders — первым: дальше по пайплайну RemoteIpAddress и IsHttps уже реальные.
    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    // Заголовки безопасности — рано, чтобы попадали и на ответы-ошибки.
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<RequestIdEnricherMiddleware>();
    app.UseSerilogRequestLogging();

    // Swagger UI — исключительно в Development.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors(corsPolicy);

    app.UseAuthentication();
    app.UseAuthorization();

    // Rate-limit после аутентификации: политики «на пользователя» видят identity.
    app.UseRateLimiter();

    app.MapControllers();

    // ---- Health checks ----
    // Публичны: FallbackPolicy их бы закрыл, поэтому явно AllowAnonymous.
    // /health/live — процесс жив, зависимости не проверяем.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false
    }).AllowAnonymous().DisableRateLimiting();

    // /health/ready — готовность: Postgres и MinIO (тег "ready").
    // Тело — только сводный статус ("Healthy"/"Unhealthy"). Детальный ответ
    // (имена проверок, тексты исключений) наружу не отдаём: эндпоинт анонимный,
    // и это была бы бесплатная разведка стека и его состояния.
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).AllowAnonymous().DisableRateLimiting();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Приложение упало при старте");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Нужно для WebApplicationFactory в интеграционных тестах.
public partial class Program;
