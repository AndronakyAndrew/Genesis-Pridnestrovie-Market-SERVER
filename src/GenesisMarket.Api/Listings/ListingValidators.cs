using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

public sealed class CreateListingRequestValidator : AbstractValidator<CreateListingRequest>
{
    public CreateListingRequestValidator(IOptions<ListingOptions> options)
    {
        RuleFor(x => x.Title).NotEmpty().MinimumLength(10).MaximumLength(100);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(20).MaximumLength(5000);
        RuleFor(x => x.District).MaximumLength(100);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.City).IsInEnum();
        RuleFor(x => x.Condition).IsInEnum();
        RuleFor(x => x.PriceType).IsInEnum();
        RuleFor(x => x.Price)
            .InclusiveBetween(0m, (decimal)options.Value.MaxPrice)
            .When(x => x.Price.HasValue);
        RuleFor(x => x)
            .Must(x => ListingRules.PriceMatchesType(x.PriceType, x.Price))
            .WithMessage("Цена не соответствует выбранному типу цены");
    }
}

public sealed class UpdateListingRequestValidator : AbstractValidator<UpdateListingRequest>
{
    public UpdateListingRequestValidator(IOptions<ListingOptions> options)
    {
        RuleFor(x => x.Title).NotEmpty().MinimumLength(10).MaximumLength(100);
        RuleFor(x => x.Description).NotEmpty().MinimumLength(20).MaximumLength(5000);
        RuleFor(x => x.District).MaximumLength(100);
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.City).IsInEnum();
        RuleFor(x => x.Condition).IsInEnum();
        RuleFor(x => x.PriceType).IsInEnum();
        RuleFor(x => x.Price)
            .InclusiveBetween(0m, (decimal)options.Value.MaxPrice)
            .When(x => x.Price.HasValue);
        RuleFor(x => x)
            .Must(x => ListingRules.PriceMatchesType(x.PriceType, x.Price))
            .WithMessage("Цена не соответствует выбранному типу цены");
    }
}

public static class ListingRules
{
    /// <summary>Free ⇒ 0, Negotiable ⇒ null, Fixed ⇒ задана и ≥ 0.</summary>
    public static bool PriceMatchesType(PriceType type, decimal? price) => type switch
    {
        PriceType.Free => price == 0,
        PriceType.Negotiable => price is null,
        PriceType.Fixed => price is not null && price >= 0,
        _ => false
    };

    /// <summary>Нормализация Title для проверки дубликатов: lower + trim + схлопнутые пробелы.</summary>
    public static string NormalizeTitle(string title) =>
        string.Join(' ', title.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
