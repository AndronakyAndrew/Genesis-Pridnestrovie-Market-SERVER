using System.Linq.Expressions;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Business;

/// <summary>
/// Продавец в карточке объявления: «ID профиля» и бейдж подтверждённого бизнеса.
/// Реквизитов (номер, адрес) здесь нет намеренно — они только в публичном профиле.
/// Название магазина отдаётся лишь подтверждённому бизнесу: непроверенное название
/// выглядело бы на витрине как проверенное.
/// </summary>
public sealed record SellerBadge(string PublicCode, bool IsVerifiedBusiness, string? ShopName)
{
    /// <summary>Проекция для EF — один запрос, условие бейджа транслируется в SQL.</summary>
    public static readonly Expression<Func<User, SellerBadge>> FromUser = u => new SellerBadge(
        u.PublicCode,
        u.BusinessProfile != null
            && u.BusinessProfile.AccountType == AccountType.Business
            && u.BusinessProfile.Status == BusinessVerificationStatus.Verified,
        u.BusinessProfile != null
            && u.BusinessProfile.AccountType == AccountType.Business
            && u.BusinessProfile.Status == BusinessVerificationStatus.Verified
            ? u.BusinessProfile.ShopName
            : null);
}
