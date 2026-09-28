using System.Text;
using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Auth.TwoFactor;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace GenesisMarket.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Собственная аутентификация: валидация JWT (HS256) + сервисы токенов,
    /// refresh, SMS, rate-limit и проверки SecurityStamp. ASP.NET Identity не используется.
    /// </summary>
    public static IServiceCollection AddGenesisAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<VerificationOptions>(configuration.GetSection(VerificationOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));
        services.Configure<PublishingOptions>(configuration.GetSection(PublishingOptions.Section));
        services.Configure<PhoneOptions>(configuration.GetSection(PhoneOptions.Section));
        services.Configure<PasswordResetOptions>(configuration.GetSection(PasswordResetOptions.Section));
        services.Configure<TwoFactorOptions>(configuration.GetSection(TwoFactorOptions.Section));
        services.Configure<StaffSessionOptions>(configuration.GetSection(StaffSessionOptions.Section));

        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();

        // Ключ — только из окружения. Приложение не должно стартовать без него.
        if (string.IsNullOrWhiteSpace(jwt.Key))
            throw new InvalidOperationException(
                "Jwt:Key не задан. Ключ подписи HS256 должен приходить из переменной окружения Jwt__Key.");
        if (Encoding.UTF8.GetByteCount(jwt.Key) < 32)
            throw new InvalidOperationException(
                "Jwt:Key слишком короткий: нужен минимум 256 бит (32 байта).");

        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        // Единственная точка получения текущего пользователя.
        services.AddScoped<ICurrentUser, CurrentUser>();
        // Обобщённая проверка владения ресурсом (Listing и будущие IOwnedResource).
        services.AddScoped<IAuthorizationHandler, ResourceOwnerHandler>();

        services.AddSingleton<IIpHasher, HmacIpHasher>();
        services.AddSingleton<IAuthRateLimiter, MemoryAuthRateLimiter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<SecurityStampValidator>();

        // ---- Роли, журнал действий, 2FA ----
        services.TryAddSingleton(TimeProvider.System);
        services.AddRoleManagement();
        services.AddScoped<IAuditActor, HttpAuditActor>();
        services.AddSingleton(TwoFactorProtectorFrom(configuration));
        services.AddScoped<TwoFactorService>();
        services.AddScoped<MfaChallengeService>();

        // Публичный номер аккаунта: источник случайности — синглтон без состояния,
        // сам генератор scoped (ходит в БД за проверкой занятости).
        services.AddSingleton<IPublicCodeSource, RandomPublicCodeSource>();
        services.AddScoped<IPublicCodeGenerator, PublicCodeGenerator>();
        // Перевод «ID профиля» → Guid: публичные адреса ходят по коду.
        services.AddScoped<Profiles.IPublicCodeResolver, Profiles.PublicCodeResolver>();

        // ---- Подтверждение контактов (почта/телефон) ----
        // Выбор реализации зависит НЕ ТОЛЬКО от конфигурации, но и от окружения:
        // лог-заглушки печатают код подтверждения, телефон и адрес открытым текстом,
        // поэтому вне Development они недопустимы. Пустая конфигурация вне
        // разработки — это ошибка развёртывания, а не повод тихо писать в лог.
        var isDevelopment = environment.IsDevelopment();

        // SMS-провайдера в проекте нет: в разработке код виден в логе, иначе канал
        // честно отвечает «недоступен» (503), см. UnavailableSmsSender.
        if (isDevelopment)
            services.AddSingleton<ISmsSender, DevSmsSender>();
        else
            services.AddSingleton<ISmsSender, UnavailableSmsSender>();

        if (!string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else if (isDevelopment)
        {
            services.AddSingleton<IEmailSender, LogEmailSender>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Smtp:Host не задан, а окружение — {environment.EnvironmentName}. " +
                "Без SMTP код подтверждения писался бы в лог вместе с адресом получателя. " +
                "Задайте SMTP_HOST (и SMTP_USER/SMTP_PASSWORD) в окружении.");
        }

        services.AddSingleton<VerificationEmailRenderer>();
        services.AddSingleton<PasswordResetEmailRenderer>();
        services.AddSingleton<IVerificationSender, VerificationSender>();
        services.AddScoped<VerificationService>();
        services.AddScoped<LoginCodeService>();
        services.AddScoped<PasswordResetService>();
        services.AddSingleton<IPublishingPolicy, PublishingPolicy>();

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Читаем claims как есть (sub/role/sstamp), без маппинга в длинные URI.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ClockSkew = TimeSpan.Zero,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var validator = context.HttpContext.RequestServices
                            .GetRequiredService<SecurityStampValidator>();
                        await validator.ValidateAsync(context);
                    }
                };
            });

        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, MfaAwareAuthorizationResultHandler>();

        services.AddAuthorization(options =>
        {
            // Служебная зона — только политики-разрешения. Роль приходит только из
            // claim токена (актуальность держит SecurityStamp: смена роли его ротирует),
            // разрешение действует лишь при claim mfa. Имён ролей в атрибутах нет.
            foreach (var permission in Permissions.All)
                options.AddPolicy(permission, p => p
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));

            // Забаненные не проходят валидацию токена (SecurityStampValidator),
            // поэтому «не забанен» == «аутентифицирован».
            options.AddPolicy("NotBanned", p => p.RequireAuthenticatedUser());
            // Проверка владения ресурсом.
            options.AddPolicy(ResourceOwnerRequirement.Policy,
                p => p.AddRequirements(new ResourceOwnerRequirement()));

            // Забытый [Authorize] не должен открывать эндпоинт: по умолчанию —
            // требуем аутентификацию. Публичные помечаются [AllowAnonymous] явно.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    /// <summary>
    /// Хранилище ролей, их смена и журнал действий — общая часть HTTP-приложения и
    /// CLI-команд. Исполнитель действия (<see cref="IAuditActor"/>) регистрирует вызывающий.
    /// </summary>
    public static IServiceCollection AddRoleManagement(this IServiceCollection services)
    {
        services.AddScoped<IRoleStore, DbRoleStore>();
        services.AddScoped<RoleManagementService>();
        services.AddScoped<IAuditLog, AuditLog>();
        return services;
    }

    /// <summary>
    /// Ключ шифрования секретов TOTP — только из окружения (<c>Security__TotpKey</c>),
    /// во всех окружениях: без него 2FA не может ни включиться, ни проверить код, а
    /// «тихий» запасной ключ сделал бы шифрование декоративным.
    /// </summary>
    public static ITwoFactorSecretProtector TwoFactorProtectorFrom(IConfiguration configuration)
    {
        var raw = configuration["Security:TotpKey"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Security:TotpKey не задан. Ключ шифрования 2FA (32 байта, base64) должен приходить " +
                "из переменной окружения Security__TotpKey (openssl rand -base64 32).");

        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException)
        {
            throw new InvalidOperationException("Security:TotpKey — не base64.");
        }

        return new AesGcmTwoFactorSecretProtector(key);
    }
}
