using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class SearchMissConfiguration : IEntityTypeConfiguration<SearchMiss>
{
    public void Configure(EntityTypeBuilder<SearchMiss> b)
    {
        b.ToTable("search_misses");
        b.HasKey(m => m.Id);

        b.Property(m => m.Query).HasMaxLength(100).IsRequired();

        // Для аналитики «что искали за период» и группировки популярных промахов.
        b.HasIndex(m => m.CreatedAt);
        b.HasIndex(m => m.Query);
    }
}
