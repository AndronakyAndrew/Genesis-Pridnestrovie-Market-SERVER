using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace GenesisMarket.Api.Auth;

/// <summary>Требование: доступ к ресурсу только владельцу или модератору/админу.</summary>
public sealed class ResourceOwnerRequirement : IAuthorizationRequirement
{
    public const string Policy = "ResourceOwner";
}

/// <summary>
/// Обобщённый хендлер владения: срабатывает для любого <see cref="IOwnedResource"/>
/// (сейчас Listing; позже Review, SavedSearch — им достаточно реализовать интерфейс).
/// Разрешает доступ владельцу ЛИБО модератору/админу.
/// </summary>
public sealed class ResourceOwnerHandler(ICurrentUser currentUser, ISecurityAudit securityAudit)
    : AuthorizationHandler<ResourceOwnerRequirement, IOwnedResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOwnerRequirement requirement,
        IOwnedResource resource)
    {
        if (!currentUser.IsAuthenticated)
            return Task.CompletedTask; // не Succeed — доступ запрещён

        var isModerator = currentUser.Role is UserRole.Moderator or UserRole.Admin;
        var isOwner = currentUser.UserId is { } id && id == resource.OwnerId;

        if (isModerator || isOwner)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Аутентифицирован, но не владелец и не модератор — попытка доступа к чужому
        // ресурсу (потенциальный IDOR). Фиксируем в журнале безопасности.
        if (currentUser.UserId is { } actor)
        {
            var resourceId = resource is BaseEntity entity ? entity.Id : Guid.Empty;
            securityAudit.ResourceForbidden(actor, resource.GetType().Name, resourceId);
        }

        return Task.CompletedTask;
    }
}
