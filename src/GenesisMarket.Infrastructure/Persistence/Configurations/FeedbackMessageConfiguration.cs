using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class FeedbackMessageConfiguration : IEntityTypeConfiguration<FeedbackMessage>
{
    public void Configure(EntityTypeBuilder<FeedbackMessage> b)
    {
        b.ToTable("feedback_messages", t =>
        {
            t.HasCheckConstraint("ck_feedback_messages_message_length",
                "char_length(\"Message\") <= 4000");
        });

        b.HasKey(f => f.Id);

        b.Property(f => f.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(f => f.Name).HasMaxLength(200);
        b.Property(f => f.Contact).HasMaxLength(320).IsRequired();
        b.Property(f => f.Message).HasMaxLength(4000).IsRequired();

        // Свежие обращения — для просмотра модераторами/поддержкой.
        b.HasIndex(f => f.CreatedAt);
    }
}
