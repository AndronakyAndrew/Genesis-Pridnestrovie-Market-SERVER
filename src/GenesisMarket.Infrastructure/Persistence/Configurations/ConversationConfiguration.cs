using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations");
        b.HasKey(c => c.Id);

        b.HasOne(c => c.Listing)
            .WithMany()
            .HasForeignKey(c => c.ListingId)
            .OnDelete(DeleteBehavior.Restrict);

        // Две FK на users — обе Restrict (иначе множественные каскадные пути).
        b.HasOne(c => c.Buyer)
            .WithMany()
            .HasForeignKey(c => c.BuyerId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(c => c.Seller)
            .WithMany()
            .HasForeignKey(c => c.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Один диалог на пару (объявление, покупатель).
        b.HasIndex(c => new { c.ListingId, c.BuyerId }).IsUnique();
    }
}
