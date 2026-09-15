using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ChannelPostQueueConfiguration : IEntityTypeConfiguration<ChannelPostQueue>
{
    public void Configure(EntityTypeBuilder<ChannelPostQueue> b)
    {
        b.ToTable("channel_post_queue", t =>
        {
            // Счётчик попыток не бывает отрицательным.
            t.HasCheckConstraint(
                "ck_channel_post_queue_attempts_nonnegative",
                "\"AttemptCount\" >= 0");
        });
        b.HasKey(q => q.Id);

        // Статус — строкой (операционный enum, как у outbox, не native enum каталога).
        b.Property(q => q.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(Domain.Enums.ChannelPostStatus.Pending)
            .IsRequired();

        b.Property(q => q.LastError).HasMaxLength(2048);

        // Каскадное удаление вместе с объявлением: без объявления строка очереди бессмысленна.
        b.HasOne(q => q.Listing)
            .WithOne()
            .HasForeignKey<ChannelPostQueue>(q => q.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Одно объявление публикуется в канал один раз — гарантия на уровне БД.
        b.HasIndex(q => q.ListingId).IsUnique();

        // Выборка очереди: по статусу, FIFO по времени постановки.
        b.HasIndex(q => new { q.Status, q.EnqueuedAt });
    }
}
