using System.Net;
using System.Text.Json;
using GenesisMarket.Api.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Служебный бот не должен ронять площадку: без токена, с кривым токеном или с токеном,
/// который Telegram отклонил, воркер пишет warning и завершается. В сеть тесты не ходят.
/// </summary>
public class TelegramBotTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string WellFormedToken = "123456789:AAFakeTokenForTestsOnly_0123456789abc";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("123456789:short")]
    [InlineData(WellFormedToken + "\r")]
    public void BotTokenProblem_reports_missing_or_malformed_token(string token)
    {
        var problem = new TelegramOptions { BotToken = token }.BotTokenProblem();

        Assert.NotNull(problem);
        Assert.DoesNotContain(token.Trim().Length > 0 ? token.Trim() : "\0", problem);
    }

    [Fact]
    public void BotTokenProblem_accepts_well_formed_token() =>
        Assert.Null(new TelegramOptions { BotToken = WellFormedToken }.BotTokenProblem());

    [Fact]
    public async Task Worker_without_token_logs_warning_and_exits_without_network()
    {
        var tg = new ScriptedTelegramClient();
        var log = new ListLogger<TelegramUpdateWorker>();

        await RunToCompletionAsync(new TelegramUpdateWorker(tg,
            Options.Create(new TelegramOptions { AdminChatId = 42 }), log));

        Assert.Empty(tg.Calls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("BotToken"));
    }

    [Fact]
    public async Task Worker_without_admin_chat_logs_warning_and_exits_without_network()
    {
        var tg = new ScriptedTelegramClient();
        var log = new ListLogger<TelegramUpdateWorker>();

        await RunToCompletionAsync(new TelegramUpdateWorker(tg,
            Options.Create(new TelegramOptions { BotToken = WellFormedToken }), log));

        Assert.Empty(tg.Calls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("AdminChatId"));
    }

    [Fact]
    public async Task Worker_stops_polling_when_telegram_rejects_token()
    {
        var tg = new ScriptedTelegramClient { GetMeError = new TelegramApiException(401, "Unauthorized") };
        var log = new ListLogger<TelegramUpdateWorker>();

        await RunToCompletionAsync(new TelegramUpdateWorker(tg,
            Options.Create(new TelegramOptions { BotToken = WellFormedToken, AdminChatId = 42 }), log));

        Assert.Equal(["getMe"], tg.Calls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("отклонил токен"));
    }

    [Fact]
    public async Task Worker_survives_unbindable_configuration()
    {
        var tg = new ScriptedTelegramClient();
        var log = new ListLogger<TelegramUpdateWorker>();

        // Так ведёт себя IOptions, когда в Telegram__AdminChatId лежит не число.
        await RunToCompletionAsync(new TelegramUpdateWorker(tg, new ThrowingOptions(), log));

        Assert.Empty(tg.Calls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task App_without_token_starts_and_smoke_endpoint_reports_503()
    {
        var client = factory
            .WithWebHostBuilder(b => b
                .UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Telegram:BotToken"] = "" })))
            .CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var me = await client.GetAsync(TelegramDiagnosticsEndpoints.Route);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, me.StatusCode);
        Assert.Equal("application/problem+json", me.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task App_with_token_resolves_bot_client_alongside_outbox_client()
    {
        // С токеном outbox регистрирует свой HTTP-клиент — как на проде. AddOutbox читает токен
        // ещё до Build, поэтому только через окружение; polling выключен, чтобы не идти в сеть.
        using var env = new EnvScope(
            ("Telegram__BotToken", WellFormedToken), ("Telegram__EnableUpdatePolling", "false"));
        await using var app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        using var scope = app.Services.CreateScope();

        Assert.IsType<TelegramClient>(scope.ServiceProvider.GetRequiredService<ITelegramClient>());
        Assert.IsType<GenesisMarket.Api.Outbox.Telegram.HttpTelegramClient>(
            scope.ServiceProvider.GetRequiredService<GenesisMarket.Api.Outbox.Telegram.ITelegramClient>());
    }

    private static async Task RunToCompletionAsync(TelegramUpdateWorker worker)
    {
        await worker.StartAsync(CancellationToken.None);
        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class ScriptedTelegramClient : ITelegramClient
    {
        public List<string> Calls { get; } = [];
        public Exception? GetMeError { get; init; }

        public Task<TelegramBotInfo> GetMeAsync(CancellationToken ct = default)
        {
            Calls.Add("getMe");
            return GetMeError is null
                ? Task.FromResult(new TelegramBotInfo(1, "genesis_test_bot", "Genesis"))
                : Task.FromException<TelegramBotInfo>(GetMeError);
        }

        public Task<JsonElement> CallAsync(string method, object? payload, CancellationToken ct = default)
        {
            Calls.Add(method);
            // Настоящую сеть не имитируем: если воркер дошёл до getUpdates, тест это увидит в Calls.
            return Task.FromException<JsonElement>(new InvalidOperationException("сеть в тесте недоступна"));
        }

        public Task<int> SendMessageAsync(string chatId, string text, InlineButton? button = null,
            bool disablePreview = true, CancellationToken ct = default) => Record<int>("sendMessage");

        public Task<int> SendPhotoAsync(string chatId, string photoUrl, string caption,
            InlineButton? button = null, CancellationToken ct = default) => Record<int>("sendPhoto");

        public Task EditCaptionAsync(string chatId, int messageId, string caption,
            InlineButton? button = null, CancellationToken ct = default) => Record<int>("editMessageCaption");

        public Task DeleteMessageAsync(string chatId, int messageId, CancellationToken ct = default) =>
            Record<int>("deleteMessage");

        private Task<T> Record<T>(string method)
        {
            Calls.Add(method);
            return Task.FromResult(default(T)!);
        }
    }

    private sealed class ThrowingOptions : IOptions<TelegramOptions>
    {
        public TelegramOptions Value => throw new InvalidOperationException(
            "Failed to convert configuration value at 'Telegram:AdminChatId' to type 'System.Int64'.");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
