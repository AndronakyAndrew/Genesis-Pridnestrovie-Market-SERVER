using GenesisMarket.Api.Middleware;
using GenesisMarket.Infrastructure;
using HealthChecks.UI.Client;
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
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

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

    // ---- Инфраструктура: PostgreSQL + MinIO + их health checks ----
    builder.Services.AddInfrastructure(builder.Configuration);

    // ---- ProblemDetails + глобальный обработчик исключений ----
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddControllers();

    // ---- Swagger: генерируем только вне Production ----
    if (!builder.Environment.IsProduction())
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
    }

    var app = builder.Build();

    // Порядок middleware важен: исключения ловим первыми.
    app.UseExceptionHandler();
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

    app.MapControllers();

    // ---- Health checks ----
    // /health/live — процесс жив, зависимости не проверяем.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false
    });

    // /health/ready — готовность: Postgres и MinIO (тег "ready").
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

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
