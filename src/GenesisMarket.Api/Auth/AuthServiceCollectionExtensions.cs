using System.Text;
using GenesisMarket.Domain.Enums;
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
        IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<VerificationOptions>(configuration.GetSection(VerificationOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));
        services.Configure<PublishingOptions>(configuration.GetSection(PublishingOptions.Section));
        services.Configure<PhoneOptions>(configuration.GetSection(PhoneOptions.Section));

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

        // ---- Подтверждение контактов (почта/телефон) ----
        // SMS пока заглушка (лог); e-mail — реальный SMTP, если задан Smtp:Host,
        // иначе dev-лог (код виден локально).
        services.AddSingleton<ISmsSender, DevSmsSender>();
        if (string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
            services.AddSingleton<IEmailSender, LogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddSingleton<VerificationEmailRenderer>();
        services.AddSingleton<IVerificationSender, VerificationSender>();
        services.AddScoped<VerificationService>();
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

        services.AddAuthorization(options =>
        {
            // Роли приходят только из claim токена.
            options.AddPolicy("Moderator", p => p.RequireRole(
                nameof(UserRole.Moderator), nameof(UserRole.Admin)));
            options.AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)));
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
}
