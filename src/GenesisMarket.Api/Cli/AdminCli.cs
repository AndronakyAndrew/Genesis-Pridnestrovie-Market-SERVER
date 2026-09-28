using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Cli;

/// <summary>
/// Аварийные консольные команды — чтобы не править БД руками:
/// <code>
/// dotnet run --project src/GenesisMarket.Api -- grant-role &lt;email&gt; &lt;role&gt; [--transfer] [--reason "…"] [--yes]
/// dotnet run --project src/GenesisMarket.Api -- reset-2fa &lt;email&gt; [--reason "…"] [--yes]
/// </code>
/// Поднимается только DI с базой (ни веб-сервера, ни Quartz, ни MinIO). Правила и
/// журнал — те же, что у интерфейса: <see cref="RoleManagementService"/> и
/// <see cref="IAuditLog"/>, исполнитель в журнале — <c>cli</c> с учётной записью ОС.
/// </summary>
public static class AdminCli
{
    private static readonly string[] Commands = ["grant-role", "reset-2fa"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    public static async Task<int> RunAsync(string[] args)
    {
        var options = CliArgs.Parse(args.Skip(1));

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            // appsettings*.json лежат рядом со сборкой — независимо от того, откуда вызвана команда.
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        // Предупреждения валидации модели EF к консольной команде отношения не имеют.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);

        var services = builder.Services;
        services.AddGenesisDbContext(builder.Configuration);
        services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
        services.Configure<StaffSessionOptions>(builder.Configuration.GetSection(StaffSessionOptions.Section));
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddSingleton<IIpHasher, HmacIpHasher>();
        services.AddScoped<ISecurityAudit, SecurityAudit>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<SecurityStampValidator>();
        services.AddRoleManagement();
        services.AddScoped<IAuditActor, CliAuditActor>();

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;

        try
        {
            return args[0] switch
            {
                "grant-role" => await GrantRoleAsync(sp, options),
                "reset-2fa" => await ResetTwoFactorAsync(sp, builder.Configuration, options),
                _ => Usage()
            };
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Ошибка: {ex.GetBaseException().Message}");
            return 1;
        }
    }

    private static async Task<int> GrantRoleAsync(IServiceProvider sp, CliArgs a)
    {
        if (a.Positional.Count != 2 || !Enum.TryParse<UserRole>(a.Positional[1], ignoreCase: true, out var role) ||
            !Enum.IsDefined(role))
            return Usage();

        var email = a.Positional[0];
        var roles = sp.GetRequiredService<IRoleStore>();
        var db = sp.GetRequiredService<AppDbContext>();

        var target = await db.Users.AsNoTracking()
            .Where(u => u.Email == email.Trim().ToLowerInvariant() && !u.IsDeleted)
            .Select(u => new { u.Id, u.PublicCode })
            .FirstOrDefaultAsync();
        if (target is null)
        {
            Console.Error.WriteLine($"Пользователь {email} не найден.");
            return 1;
        }

        var current = await roles.GetRoleAsync(target.Id, default);
        Console.WriteLine($"Пользователь #{target.PublicCode}: {current} → {role}. Все его сессии будут завершены.");
        if (role == UserRole.Owner && a.Transfer)
            Console.WriteLine("Нынешний владелец станет Admin.");
        if (!Confirm(a))
            return 1;

        var result = await sp.GetRequiredService<RoleManagementService>()
            .ChangeFromCliAsync(email, role, a.Transfer, a.Reason, default);

        switch (result.Status)
        {
            case RoleChangeStatus.Ok:
                Console.WriteLine($"Готово: {result.OldRole} → {result.NewRole}. Запись в журнале действий создана.");
                if (RolePermissions.IsStaff(role))
                    Console.WriteLine("Служебный раздел откроется после настройки 2FA в профиле.");
                return 0;
            case RoleChangeStatus.NoChange:
                Console.WriteLine($"У пользователя уже роль {result.OldRole}. Ничего не изменено.");
                return 0;
            case RoleChangeStatus.OwnerExists:
                Console.Error.WriteLine("Владелец уже назначен. Для передачи владения добавьте --transfer.");
                return 1;
            default:
                Console.Error.WriteLine($"Не выполнено: {result.Status}.");
                return 1;
        }
    }

    private static async Task<int> ResetTwoFactorAsync(IServiceProvider sp, IConfiguration configuration, CliArgs a)
    {
        if (a.Positional.Count != 1)
            return Usage();

        var db = sp.GetRequiredService<AppDbContext>();
        var email = a.Positional[0].Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted);
        if (user is null)
        {
            Console.Error.WriteLine($"Пользователь {a.Positional[0]} не найден.");
            return 1;
        }

        Console.WriteLine($"Сброс 2FA пользователя #{user.PublicCode}. Все его сессии будут завершены.");
        if (!Confirm(a))
            return 1;

        var twoFactor = new TwoFactorService(db, AuthServiceCollectionExtensions.TwoFactorProtectorFrom(configuration),
            sp.GetRequiredService<IAuditLog>(), Microsoft.Extensions.Options.Options.Create(new TwoFactorOptions()),
            TimeProvider.System);

        await using var tx = await db.Database.BeginTransactionAsync();
        if (!await twoFactor.ResetAsync(user.Id, a.Reason, default))
        {
            Console.WriteLine("2FA у пользователя не настроена. Ничего не изменено.");
            return 0;
        }

        // Сброс без выхода оставил бы сессии того, кто, возможно, завладел устройством.
        user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await sp.GetRequiredService<IRefreshTokenService>().RevokeAllAsync(user.Id, default);
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        Console.WriteLine("Готово: 2FA сброшена, сессии завершены. При следующем входе её нужно настроить заново.");
        return 0;
    }

    /// <summary>
    /// Подтверждение: <c>--yes</c> или ответ «y» в интерактивной консоли. Без терминала
    /// и без <c>--yes</c> команда не выполняется — а не зависает на чтении stdin.
    /// </summary>
    private static bool Confirm(CliArgs a)
    {
        if (a.Yes)
            return true;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Нет интерактивной консоли — подтвердите флагом --yes.");
            return false;
        }

        Console.Write("Продолжить? [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        if (answer is "y" or "Y" or "д" or "Д")
            return true;

        Console.WriteLine("Отменено.");
        return false;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Использование:
              grant-role <email> <User|Moderator|Admin|Owner> [--transfer] [--reason "…"] [--yes]
                  Назначить роль. Owner, когда владелец уже есть, — только с --transfer
                  (прежний владелец станет Admin). User — снять служебную роль.
              reset-2fa <email> [--reason "…"] [--yes]
                  Сбросить 2FA (потерян телефон и резервные коды). Сессии завершаются.
            """);
        return 2;
    }

    private sealed record CliArgs(List<string> Positional, bool Transfer, bool Yes, string? Reason)
    {
        public static CliArgs Parse(IEnumerable<string> raw)
        {
            var positional = new List<string>();
            bool transfer = false, yes = false;
            string? reason = null;

            using var e = raw.GetEnumerator();
            while (e.MoveNext())
            {
                switch (e.Current)
                {
                    case "--transfer": transfer = true; break;
                    case "--yes" or "-y": yes = true; break;
                    case "--reason": reason = e.MoveNext() ? e.Current : null; break;
                    default: positional.Add(e.Current); break;
                }
            }

            return new CliArgs(positional, transfer, yes, reason);
        }
    }
}
