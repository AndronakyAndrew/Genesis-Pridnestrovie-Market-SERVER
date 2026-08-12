using GenesisMarket.Api.Outbox;
using GenesisMarket.Domain.Entities;

namespace GenesisMarket.Tests;

/// <summary>
/// Тестовый обработчик: всегда бросает транзиентную ошибку. Текст ошибки намеренно
/// содержит email и телефон — так проверяется, что диспетчер вычищает персональные
/// данные из поля Error и логов. Триггерится только сообщениями типа <see cref="FailType"/>.
/// </summary>
public sealed class TestFailingOutboxHandler : IOutboxHandler
{
    public const string FailType = "test-fail";

    /// <summary>Строка с PII, которую scrubber обязан вычистить.</summary>
    public const string LeakyMessage = "SMTP отказал для user@example.com телефон +37312345678";

    public string Type => FailType;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct) =>
        throw new InvalidOperationException(LeakyMessage);
}
