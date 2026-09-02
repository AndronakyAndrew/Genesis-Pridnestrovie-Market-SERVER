using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Обращение из формы обратной связи. Приём открыт анонимам — форма не требует входа.
/// После сохранения ставится сообщение в outbox: письмо-уведомление на служебный адрес
/// (и, если контакт похож на email, короткое письмо-подтверждение отправителю).
/// </summary>
public class FeedbackMessage : BaseEntity
{
    public FeedbackType Type { get; set; }

    /// <summary>Имя отправителя. Опционально.</summary>
    public string? Name { get; set; }

    /// <summary>Контакт для ответа — email или телефон, как ввёл отправитель.</summary>
    public required string Contact { get; set; }

    public required string Message { get; set; }
}
