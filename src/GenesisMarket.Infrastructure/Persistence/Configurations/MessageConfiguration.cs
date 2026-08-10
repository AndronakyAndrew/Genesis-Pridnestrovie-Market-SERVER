using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages", t =>
            t.HasCheckConstraint(
                "ck_messages_text_length",
                "char_length(\"Text\") <= 2000"));

        b.HasKey(m => m.Id);

        b.Property(m => m.Text).HasMaxLength(2000).IsRequired();

        b.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(m => m.Sender)
            .WithMany()
            .HasForeignKey(m => m.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Лента диалога по времени.
        b.HasIndex(m => new { m.ConversationId, m.CreatedAt });
    }
}
