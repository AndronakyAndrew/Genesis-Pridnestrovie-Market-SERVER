using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GenesisMarket.Api.Observability;

/// <summary>
/// OpenTelemetry: трейсы (ASP.NET Core + HttpClient + EF Core) и метрики
/// (ASP.NET Core + HttpClient + рантайм). Экспорт по OTLP включается ТОЛЬКО если задан
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> — иначе инструментация работает вхолостую и не
/// пытается достучаться до несуществующего коллектора.
/// </summary>
public static class ObservabilitySetup
{
    public static IServiceCollection AddGenesisObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        var serviceName = configuration["OpenTelemetry:ServiceName"] ?? "genesis-market-api";
        var otlpConfigured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation();
                if (otlpConfigured)
                    tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (otlpConfigured)
                    metrics.AddOtlpExporter();
            });

        return services;
    }
}
