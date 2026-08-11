using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> b)
    {
        b.ToTable("reviews", t =>
        {
            // Диапазон оценки и длина текста — на уровне БД, а не только атрибутами DTO.
            t.HasCheckConstraint("ck_reviews_rating_range", "\"Rating\" >= 1 AND \"Rating\" <= 5");
            t.HasCheckConstraint("ck_reviews_text_length", "char_length(\"Text\") <= 1000");
        });

        b.HasKey(r => r.Id);

        b.Property(r => r.Text).HasMaxLength(1000).IsRequired();
        b.Property(r => r.IsHidden).HasDefaultValue(false);

        // Один отзыв на пару (автор, объявление) — анти-накрутка на уровне БД.
        b.HasIndex(r => new { r.AuthorId, r.ListingId }).IsUnique();

        // Публичная выдача отзывов о продавце: свежие сверху, скрытые отфильтрованы
        // частичным индексом (покрывает WHERE TargetUserId + ORDER BY CreatedAt,Id DESC).
        b.HasIndex(r => new { r.TargetUserId, r.CreatedAt, r.Id })
            .IsDescending(false, true, true)
            .HasFilter("\"IsHidden\" = false");

        // Объявление и автор — с FK, но без каскадного удаления: и объявления, и
        // аккаунты удаляются мягко (архивация/анонимизация), отзывы не должны исчезать.
        b.HasOne(r => r.Listing)
            .WithMany()
            .HasForeignKey(r => r.ListingId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(r => r.Author)
            .WithMany()
            .HasForeignKey(r => r.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);

        // TargetUserId — FK на users для целостности (навигацию не заводим:
        // агрегат рейтинга живёт денормализованно в users, триггер его считает).
        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
