using GenesisMarket.Domain.Entities;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

/// <summary>Какое подтверждение обязательно для публикации объявлений.</summary>
public enum RequiredVerification
{
    None,
    Email,
    Phone,
    Both
}

/// <summary>Секция <c>Publishing</c>. На проде — <c>Email</c> (телефон отложен).</summary>
public sealed class PublishingOptions
{
    public const string Section = "Publishing";

    public RequiredVerification RequiredVerification { get; set; } = RequiredVerification.Email;
}

public interface IPublishingPolicy
{
    /// <summary>Можно ли пользователю публиковать. Reason — текст ошибки, если нельзя.</summary>
    (bool Ok, string? Reason) CanPublish(User user);
}

public sealed class PublishingPolicy(IOptions<PublishingOptions> options) : IPublishingPolicy
{
    private const string EmailReason = "Требуется подтверждение электронной почты";
    private const string PhoneReason = "Требуется подтверждение телефона";
    private const string BothReason = "Требуется подтверждение почты и телефона";

    public (bool Ok, string? Reason) CanPublish(User user) =>
        options.Value.RequiredVerification switch
        {
            RequiredVerification.None => (true, null),
            RequiredVerification.Email => user.EmailVerified ? (true, null) : (false, EmailReason),
            RequiredVerification.Phone => user.PhoneVerified ? (true, null) : (false, PhoneReason),
            RequiredVerification.Both =>
                user.EmailVerified && user.PhoneVerified ? (true, null) : (false, BothReason),
            _ => (false, "Публикация недоступна")
        };
}
