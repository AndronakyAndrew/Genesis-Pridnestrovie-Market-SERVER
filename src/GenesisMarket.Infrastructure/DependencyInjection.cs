using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minio;

namespace GenesisMarket.Infrastructure;

public static class DependencyInjection
{
    public const string PostgresHealthTag = "ready";
    public const string MinioHealthTag = "ready";

    /// <summary>
    /// Регистрирует инфраструктуру: PostgreSQL (EF Core) и MinIO,
    /// включая их readiness health checks.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ---- PostgreSQL ----
        var postgres = BuildPostgresConnectionString(configuration);
        services.AddDbContext<AppDbContext>(o =>
            o.UseNpgsql(postgres, npgsql => npgsql.MapGenesisEnums()));

        // ---- MinIO ----
        services.Configure<MinioOptions>(configuration.GetSection(MinioOptions.SectionName));
        var minio = configuration.GetSection(MinioOptions.SectionName).Get<MinioOptions>()
                    ?? new MinioOptions();

        services.AddSingleton<IMinioClient>(_ =>
            new MinioClient()
                .WithEndpoint(minio.Endpoint)
                .WithCredentials(minio.AccessKey, minio.SecretKey)
                .WithSSL(minio.UseSsl)
                .Build());

        services.AddScoped<IObjectStorage, MinioObjectStorage>();

        // ---- Health checks (readiness) ----
        services.AddHealthChecks()
            .AddNpgSql(postgres, name: "postgres", tags: ["ready"])
            .AddCheck<MinioHealthCheck>("minio", tags: ["ready"]);

        return services;
    }

    private static string BuildPostgresConnectionString(IConfiguration configuration)
    {
        // Если явная строка задана — используем её, иначе собираем из частей.
        var explicitConn = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(explicitConn))
            return explicitConn;

        var section = configuration.GetSection("Postgres");
        return
            $"Host={section["Host"]};" +
            $"Port={section["Port"]};" +
            $"Database={section["Database"]};" +
            $"Username={section["User"]};" +
            $"Password={section["Password"]}";
    }
}
