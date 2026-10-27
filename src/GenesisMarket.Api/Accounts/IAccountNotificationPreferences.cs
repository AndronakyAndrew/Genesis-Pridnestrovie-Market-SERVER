using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.Accounts;

public interface IAccountNotificationPreferences
{
    Task<NotificationPreferencesResponse?> GetAsync(Guid userId, CancellationToken ct);
    Task<NotificationPreferencesResponse?> UpdateAsync(
        Guid userId, UpdateNotificationPreferencesRequest request, CancellationToken ct);
    Task<bool> IsEnabledAsync(Guid userId, string groupKey, CancellationToken ct);
}
