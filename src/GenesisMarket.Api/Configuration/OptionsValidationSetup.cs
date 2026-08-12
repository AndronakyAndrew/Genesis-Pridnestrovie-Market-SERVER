using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Configuration;

/// <summary>Маркер: к нему привязана валидация конфигурации при старте (ValidateOnStart).</summary>
public sealed class StartupChecks;

/// <summary>
/// Валидация конфигурации при старте: приложение не поднимается с пустым JWT-ключом
/// (это уже проверяет <c>AddGenesisAuth</c>), дефолтным паролем БД, пустым списком CORS
/// или без ключа хеширования IP в Production. Плюс инвариант «секреты только из env»:
/// в <c>appsettings*.json</c> у секретных ключей не должно быть значений.
/// </summary>
public static class OptionsValidationSetup
{
    // Ключи, значения которых обязаны приходить ТОЛЬКО из окружения, не из appsettings.
    private static readonly string[] SecretKeys =
    [
        "Jwt:Key", "Postgres:Password", "Minio:SecretKey",
        "Smtp:Password", "Security:IpHashKey", "Telegram:BotToken",
    ];

    private static readonly string[] DefaultDbPasswords = ["", "genesis", "postgres", "password"];

    public static IServiceCollection AddGenesisOptionsValidation(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<StartupChecks>>(
            new StartupChecksValidator(configuration, environment));
        services.AddOptions<StartupChecks>().ValidateOnStart();
        return services;
    }

    private sealed class StartupChecksValidator(IConfiguration configuration, IHostEnvironment environment)
        : IValidateOptions<StartupChecks>
    {
        public ValidateOptionsResult Validate(string? name, StartupChecks options)
        {
            var errors = new List<string>();

            // «Секреты только из env» — во всех окружениях: в appsettings их быть не должно.
            errors.AddRange(ScanAppSettingsForSecrets());

            // Остальные требования жёстки только в Production (dev/тесты работают на дефолтах).
            if (environment.IsProduction())
            {
                // Пароль БД: не пустой и не дефолтный (если строка подключения не задана целиком).
                if (string.IsNullOrEmpty(configuration["ConnectionStrings:Postgres"]))
                {
                    var dbPassword = configuration["Postgres:Password"] ?? "";
                    if (DefaultDbPasswords.Contains(dbPassword, StringComparer.OrdinalIgnoreCase))
                        errors.Add("Postgres:Password пуст или дефолтный — задайте надёжный пароль через окружение.");
                }

                // Ключ хеширования IP: без него IP не хешируется, журнал/rate-limit теряют партицию.
                if (string.IsNullOrWhiteSpace(configuration["Security:IpHashKey"]))
                    errors.Add("Security:IpHashKey не задан — обязателен в Production (через окружение).");

                // CORS: явный белый список абсолютных origin без завершающего слэша.
                var origins = (configuration["Cors:AllowedOrigins"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (origins.Length == 0)
                    errors.Add("Cors:AllowedOrigins пуст — в Production нужен явный белый список origin.");
                foreach (var origin in origins)
                {
                    if (!Uri.TryCreate(origin, UriKind.Absolute, out _))
                        errors.Add($"Cors:AllowedOrigins: '{origin}' — не абсолютный URI.");
                    else if (origin.EndsWith('/'))
                        errors.Add($"Cors:AllowedOrigins: '{origin}' — уберите завершающий слэш.");
                }
            }

            return errors.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(errors);
        }

        // Проверяет, что у секретных ключей нет непустого значения в закоммиченных
        // appsettings — они должны приходить из окружения. Базовый appsettings.json
        // (используется в Production) проверяется всегда; локальный appsettings.Development.json
        // с dev-кредами — исключение (не Production, файл только для локальной разработки).
        private IEnumerable<string> ScanAppSettingsForSecrets()
        {
            var files = new List<string> { "appsettings.json" };
            if (!environment.IsDevelopment())
                files.Add($"appsettings.{environment.EnvironmentName}.json");

            foreach (var file in files)
            {
                var path = Path.Combine(environment.ContentRootPath, file);
                if (!File.Exists(path))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(File.ReadAllText(path));
                }
                catch (JsonException)
                {
                    continue; // Битый JSON поймает сам конфигуратор — здесь не наша ответственность.
                }

                using (doc)
                {
                    foreach (var key in SecretKeys)
                        if (HasNonEmptyValue(doc.RootElement, key.Split(':')))
                            yield return $"Секрет '{key}' задан в {file} — секреты допустимы только в переменных окружения.";
                }
            }
        }

        private static bool HasNonEmptyValue(JsonElement element, ReadOnlySpan<string> path)
        {
            var current = element;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment, out var next))
                    return false;
                current = next;
            }

            return current.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(current.GetString());
        }
    }
}
