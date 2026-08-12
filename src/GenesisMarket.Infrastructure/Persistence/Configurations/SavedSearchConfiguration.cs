using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class SavedSearchConfiguration : IEntityTypeConfiguration<SavedSearch>
{
    public void Configure(EntityTypeBuilder<SavedSearch> b)
    {
        b.ToTable("saved_searches", t =>
        {
            t.HasCheckConstraint("ck_saved_searches_name_length",
                "char_length(\"Name\") >= 1 AND char_length(\"Name\") <= 100");
        });

        b.HasKey(s => s.Id);

        b.Property(s => s.Name).HasMaxLength(100).IsRequired();

        // Критерии — jsonb: индексируемо и валидируется как JSON на уровне БД.
        b.Property(s => s.QueryJson).HasColumnType("jsonb").IsRequired();

        // Канал уведомлений — строкой (не входит в native enum-ы каталога).
        b.Property(s => s.NotifyChannel).HasConversion<string>().HasMaxLength(20).IsRequired();

        b.Property(s => s.IsActive).HasDefaultValue(true);

        // Удаление пользователя уносит его сохранённые поиски.
        b.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // «Мои поиски» и подсчёт активных для лимита.
        b.HasIndex(s => s.UserId);

        // Выборка джобом: активные поиски, готовые к рассылке. Частичный индекс — только активные.
        b.HasIndex(s => new { s.IsActive, s.NotifiedAt }).HasFilter("\"IsActive\"");
    }
}
