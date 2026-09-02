using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCityKamenka : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .Annotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk,kamenka")
                .Annotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .Annotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .Annotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .OldAnnotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .OldAnnotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .OldAnnotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .OldAnnotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .Annotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .Annotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .Annotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .Annotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .OldAnnotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk,kamenka")
                .OldAnnotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .OldAnnotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .OldAnnotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
