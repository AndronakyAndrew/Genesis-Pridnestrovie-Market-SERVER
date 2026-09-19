using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class BusinessProfileConfiguration : IEntityTypeConfiguration<BusinessProfile>
{
    public void Configure(EntityTypeBuilder<BusinessProfile> b)
    {
        b.ToTable("business_profiles", t =>
        {
            // Pending и Verified возможны только во включённом бизнес-режиме.
            // Заодно ловит гонку «модератор одобряет — пользователь выключил режим».
            t.HasCheckConstraint("ck_business_profiles_status_requires_business",
                "\"Status\" IN ('None', 'Rejected') OR \"AccountType\" = 'Business'");

            // У заявки на рассмотрении есть момент подачи.
            t.HasCheckConstraint("ck_business_profiles_pending_submitted",
                "\"Status\" <> 'Pending' OR \"SubmittedAt\" IS NOT NULL");

            // Решение модератора всегда с автором и датой.
            t.HasCheckConstraint("ck_business_profiles_decision_attributed",
                "\"Status\" NOT IN ('Verified', 'Rejected') OR (\"ReviewedAt\" IS NOT NULL AND \"ReviewedByUserId\" IS NOT NULL)");

            // Отказ без причины пользователю ничего не объясняет.
            t.HasCheckConstraint("ck_business_profiles_rejection_reason",
                "\"Status\" <> 'Rejected' OR \"RejectionReason\" IS NOT NULL");
        });

        // PK = FK на users.Id (общий ключ 1:1, как у profiles).
        b.HasKey(x => x.UserId);

        b.HasOne(x => x.User)
            .WithOne(u => u.BusinessProfile)
            .HasForeignKey<BusinessProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Enum-ы режима и проверки — не native, хранятся строкой (как Role, ReportStatus).
        b.Property(x => x.AccountType).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(x => x.LegalForm).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Статус — токен конкурентности: каждый UPDATE из SaveChanges получает
        // WHERE "Status" = <прочитанное значение>. Правка реквизитов, подача и
        // отключение режима, пересёкшиеся друг с другом или с решением модератора,
        // не перетрут чужой переход, а получат DbUpdateConcurrencyException (→ 409).
        // DDL не порождает — колонку xmin ради одной таблицы не заводим.
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired().IsConcurrencyToken();

        b.Property(x => x.ShopName).HasMaxLength(80).IsRequired();
        b.Property(x => x.RegistrationNumber).HasMaxLength(32).IsRequired();
        b.Property(x => x.PickupAddress).HasMaxLength(200).IsRequired();
        b.Property(x => x.RejectionReason).HasMaxLength(500);

        // Вычисляемое — не колонки.
        b.Ignore(x => x.IsVerifiedBusiness);
        b.Ignore(x => x.CanEditDetails);
        b.Ignore(x => x.CurrentDetails);

        // Очередь модератора: заявки по статусу в порядке подачи (FIFO).
        b.HasIndex(x => new { x.Status, x.SubmittedAt });

        // Один регистрационный номер — один подтверждённый аккаунт: иначе чужой
        // номер из публичного профиля магазина давал бы второй «подтверждённый» бейдж.
        // Неподтверждённых с тем же номером может быть сколько угодно — решает модератор.
        b.HasIndex(x => x.RegistrationNumber)
            .IsUnique()
            .HasDatabaseName("ux_business_profiles_verified_registration_number")
            .HasFilter("\"Status\" = 'Verified'");
    }
}
