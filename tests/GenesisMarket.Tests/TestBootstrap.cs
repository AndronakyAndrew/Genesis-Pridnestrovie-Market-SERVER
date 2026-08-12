using System.Runtime.CompilerServices;
using Xunit;

// Env-переменные должны существовать до сборки любого хоста (их читает Program
// при регистрации сервисов, ещё до Build). ModuleInitializer гарантирует это
// для всего тестового процесса.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GenesisMarket.Tests;

internal static class TestBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Ключ подписи — только из окружения (в appsettings его нет).
        Environment.SetEnvironmentVariable(
            "Jwt__Key", "test-only-genesis-jwt-signing-key-0123456789-abcdef");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "genesis-market");
        Environment.SetEnvironmentVariable("Jwt__Audience", "genesis-market");
        // 0 — не кэшировать снимок SecurityStamp/бана: бан действует сразу (для тестов).
        Environment.SetEnvironmentVariable("Jwt__SecurityStampCacheSeconds", "0");
        Environment.SetEnvironmentVariable(
            "Security__IpHashKey", "test-only-genesis-ip-hash-key-0123456789");
        // Планировщик Quartz в тестах не поднимаем: джоб гигиены прогоняем напрямую
        // через ICatalogHygieneService. Так тесты детерминированы и без фоновых потоков.
        Environment.SetEnvironmentVariable("Scheduling__Enabled", "false");

        // Rate-limit: у одного тестового приложения все запросы приходят с одного IP
        // ("unknown"), поэтому IP-партиционированные политики (глобальная, поиск,
        // sensitive-anon для register, создание объявлений) распустили бы функциональные
        // тесты. Поднимаем их до практически безлимитных значений. Политики contact и
        // report остаются на реальных лимитах (ContactReveal/Trust) — их проверяют
        // отдельные тесты на 429 (аноним партиционируется тем же общим IP).
        Environment.SetEnvironmentVariable("RateLimit__GlobalPerMinute", "1000000");
        Environment.SetEnvironmentVariable("RateLimit__SearchPerMinute", "1000000");
        Environment.SetEnvironmentVariable("RateLimit__SensitiveAnonPerHour", "1000000");
        Environment.SetEnvironmentVariable("RateLimit__CreateListingPerHour", "1000000");
    }
}
