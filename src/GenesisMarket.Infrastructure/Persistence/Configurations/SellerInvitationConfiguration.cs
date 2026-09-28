using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class SellerInvitationConfiguration : IEntityTypeConfiguration<SellerInvitation>
{
    public void Configure(EntityTypeBuilder<SellerInvitation> b)
    {
        b.ToTable("seller_invitations", t =>
        {
            t.HasCheckConstraint("ck_seller_invitations_shop_name", "char_length(btrim(\"ShopName\")) BETWEEN 2 AND 60");
            // Ссылка есть ⇔ приглашение ожидает принятия или истекло (истёкшую отличаем от несуществующей).
            t.HasCheckConstraint("ck_seller_invitations_token",
                "(\"Status\" IN ('Pending', 'Expired')) = (\"TokenHash\" IS NOT NULL)");
            t.HasCheckConstraint("ck_seller_invitations_accepted",
                "(\"Status\" = 'Accepted') = (\"AcceptedByUserId\" IS NOT NULL)");
        });

        b.HasKey(i => i.Id);
        b.Property(i => i.ShopName).HasMaxLength(60).IsRequired();
        b.Property(i => i.City).HasMaxLength(CatalogReferenceConfiguration.CodeMaxLength).IsRequired();
        b.Property(i => i.Category).HasMaxLength(CatalogReferenceConfiguration.CodeMaxLength).IsRequired();
        b.Property(i => i.InternalNote).HasMaxLength(1000);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Ignore(i => i.IsEditable);

        // Проверка ссылки — поиск по хешу токена; он же гарантирует уникальность.
        b.HasIndex(i => i.TokenHash).IsUnique().HasFilter("\"TokenHash\" IS NOT NULL");
        // «Мои приглашения» сотрудника и фильтр по статусу.
        b.HasIndex(i => new { i.CreatedByUserId, i.CreatedAt });
        b.HasIndex(i => new { i.Status, i.StatusChangedAt });

        b.HasOne<CatalogCity>().WithMany().HasForeignKey(i => i.City).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CatalogCategory>().WithMany().HasForeignKey(i => i.Category).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(i => i.Drafts)
            .WithOne(d => d.Invitation)
            .HasForeignKey(d => d.InvitationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class InvitationDraftConfiguration : IEntityTypeConfiguration<InvitationDraft>
{
    public void Configure(EntityTypeBuilder<InvitationDraft> b)
    {
        // Те же ограничения, что у listings: принятый черновик становится объявлением без правок.
        b.ToTable("invitation_drafts", t =>
        {
            t.HasCheckConstraint("ck_invitation_drafts_title", "char_length(\"Title\") BETWEEN 5 AND 120");
            t.HasCheckConstraint("ck_invitation_drafts_price",
                "(\"PriceType\" = 'Negotiable' AND \"Price\" IS NULL) OR " +
                "(\"PriceType\" = 'Free' AND \"Price\" = 0) OR " +
                "(\"PriceType\" = 'Fixed' AND \"Price\" IS NOT NULL AND \"Price\" >= 0)");
        });

        b.HasKey(d => d.Id);
        b.Property(d => d.Title).HasMaxLength(120).IsRequired();
        b.Property(d => d.Description).HasMaxLength(5000).IsRequired();
        b.Property(d => d.Price).HasColumnType("numeric(12,0)");
        // Тип цены и состояние — строкой: native enum-типы listings на эти таблицы не тянем.
        b.Property(d => d.PriceType).HasConversion<string>().HasMaxLength(16);
        b.Property(d => d.Condition).HasConversion<string>().HasMaxLength(16);
        b.Property(d => d.Category).HasMaxLength(CatalogReferenceConfiguration.CodeMaxLength).IsRequired();

        b.HasIndex(d => new { d.InvitationId, d.SortOrder });

        b.HasOne<CatalogCategory>().WithMany().HasForeignKey(d => d.Category).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Subcategory>().WithMany().HasForeignKey(d => d.SubcategoryId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(d => d.Photos)
            .WithOne(p => p.Draft)
            .HasForeignKey(p => p.DraftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class InvitationDraftPhotoConfiguration : IEntityTypeConfiguration<InvitationDraftPhoto>
{
    public void Configure(EntityTypeBuilder<InvitationDraftPhoto> b)
    {
        b.ToTable("invitation_draft_photos");
        b.HasKey(p => p.Id);
        b.Property(p => p.ObjectKey).HasMaxLength(300).IsRequired();
        b.Property(p => p.ThumbKey).HasMaxLength(300).IsRequired();
        b.HasIndex(p => new { p.DraftId, p.SortOrder });
    }
}

public class PublicationConsentConfiguration : IEntityTypeConfiguration<PublicationConsent>
{
    public void Configure(EntityTypeBuilder<PublicationConsent> b)
    {
        b.ToTable("publication_consents");
        b.HasKey(c => c.Id);
        b.Property(c => c.Version).HasMaxLength(40).IsRequired();
        b.Property(c => c.IpHash).HasMaxLength(128);
        // Проверка при публикации: «есть ли согласие этой версии на это объявление».
        b.HasIndex(c => new { c.ListingId, c.Version });
        b.HasIndex(c => c.UserId);
        // Без FK на listings: согласие — юридический след и переживает удаление объявления.
    }
}
