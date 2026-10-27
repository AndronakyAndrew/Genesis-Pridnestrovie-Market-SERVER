using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Accounts;

public sealed class AccountNotificationPreferences(AppDbContext db) : IAccountNotificationPreferences
{
    public async Task<NotificationPreferencesResponse?> GetAsync(Guid userId, CancellationToken ct)
    {
        var notifyVia = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (NotificationChannel?)p.NotifyVia)
            .FirstOrDefaultAsync(ct);
        if (notifyVia is null)
            return null;

        var rows = await db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);
        var saved = rows.ToDictionary(p => p.GroupKey, p => p.Enabled, StringComparer.Ordinal);
        return ToResponse(saved, notifyVia.Value);
    }

    public async Task<NotificationPreferencesResponse?> UpdateAsync(
        Guid userId, UpdateNotificationPreferencesRequest request, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
            return null;

        if (request.NotifyVia is { } channel)
            profile.NotifyVia = channel;

        foreach (var (key, enabled) in request.Groups ?? new Dictionary<string, bool>())
        {
            var preference = await db.NotificationPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.GroupKey == key, ct);
            if (enabled)
            {
                if (preference is not null)
                    db.NotificationPreferences.Remove(preference);
            }
            else if (preference is null)
            {
                db.NotificationPreferences.Add(new AccountNotificationPreference
                {
                    UserId = userId,
                    GroupKey = key,
                    Enabled = false
                });
            }
            else
            {
                preference.Enabled = false;
            }
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, ct);
    }

    public async Task<bool> IsEnabledAsync(Guid userId, string groupKey, CancellationToken ct)
    {
        if (groupKey == NotificationGroups.Security)
            return true;

        var saved = await db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.GroupKey == groupKey)
            .Select(p => (bool?)p.Enabled)
            .FirstOrDefaultAsync(ct);
        return saved ?? true;
    }

    private static NotificationPreferencesResponse ToResponse(
        IReadOnlyDictionary<string, bool> saved, NotificationChannel notifyVia) => new(
        NotificationGroups.All.Select(group => new NotificationPreferenceDto(
            group.Key,
            group.Description,
            group.CanDisable ? saved.GetValueOrDefault(group.Key, true) : true,
            group.CanDisable,
            group.Types)).ToList(),
        notifyVia);
}
