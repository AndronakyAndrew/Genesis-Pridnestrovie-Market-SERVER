using FluentValidation;
using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.Business;

/// <summary>
/// Валидация реквизитов. Проверяет НОРМАЛИЗОВАННЫЕ значения — ровно то, что
/// будет записано. Имена полей — camelCase, как в JSON запроса: по ним клиент
/// подсвечивает поле. У каждого правила свой код (<see cref="BusinessErrorCodes"/>).
/// Регистрируется сканированием сборки (AddValidatorsFromAssemblyContaining).
/// </summary>
public sealed class SaveBusinessDetailsRequestValidator : AbstractValidator<SaveBusinessDetailsRequest>
{
    public SaveBusinessDetailsRequestValidator(RegistrationNumberRule registrationNumber)
    {
        // Первое сработавшее правило поля — единственное: одно поле, одна подсветка.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => BusinessInput.NormalizeText(x.ShopName))
            .NotEmpty()
                .WithErrorCode(BusinessErrorCodes.ShopNameRequired)
                .WithMessage("Укажите название магазина")
            .Length(BusinessInput.ShopNameMin, BusinessInput.ShopNameMax)
                .WithErrorCode(BusinessErrorCodes.ShopNameLength)
                .WithMessage($"Название — от {BusinessInput.ShopNameMin} до {BusinessInput.ShopNameMax} символов")
            // Название видно на каждом объявлении: телефон в нём обошёл бы лимит раскрытия контактов.
            .Must(v => !BusinessInput.ContainsContacts(v))
                .WithErrorCode(BusinessErrorCodes.ShopNameContainsContacts)
                .WithMessage("Название не должно содержать телефон, ссылку или ник")
            // Имя поля в ответе — camelCase, как в JSON запроса.
            .OverridePropertyName("shopName");

        RuleFor(x => x.LegalForm)
            .NotNull()
                .WithErrorCode(BusinessErrorCodes.LegalFormRequired)
                .WithMessage("Выберите организационно-правовую форму")
            .IsInEnum()
                .WithErrorCode(BusinessErrorCodes.LegalFormRequired)
                .WithMessage("Выберите организационно-правовую форму")
            // Имя поля в ответе — camelCase, как в JSON запроса.
            .OverridePropertyName("legalForm");

        RuleFor(x => BusinessInput.NormalizeRegistrationNumber(x.RegistrationNumber))
            .NotEmpty()
                .WithErrorCode(BusinessErrorCodes.RegistrationNumberRequired)
                .WithMessage("Укажите регистрационный номер")
            .Must(v => registrationNumber.IsValid(v))
                .WithErrorCode(BusinessErrorCodes.RegistrationNumberFormat)
                .WithMessage("Неверный формат регистрационного номера: только цифры, группы через дефис")
            // Имя поля в ответе — camelCase, как в JSON запроса.
            .OverridePropertyName("registrationNumber");

        RuleFor(x => BusinessInput.NormalizeText(x.PickupAddress))
            .NotEmpty()
                .WithErrorCode(BusinessErrorCodes.PickupAddressRequired)
                .WithMessage("Укажите адрес точки самовывоза")
            .Length(BusinessInput.PickupAddressMin, BusinessInput.PickupAddressMax)
                .WithErrorCode(BusinessErrorCodes.PickupAddressLength)
                .WithMessage($"Адрес — от {BusinessInput.PickupAddressMin} до {BusinessInput.PickupAddressMax} символов")
            // Адрес публичен в профиле магазина — тот же довод, что у названия.
            .Must(v => !BusinessInput.ContainsContacts(v))
                .WithErrorCode(BusinessErrorCodes.PickupAddressContainsContacts)
                .WithMessage("В адресе не должно быть телефона, ссылки или ника")
            // Имя поля в ответе — camelCase, как в JSON запроса.
            .OverridePropertyName("pickupAddress");
    }
}
