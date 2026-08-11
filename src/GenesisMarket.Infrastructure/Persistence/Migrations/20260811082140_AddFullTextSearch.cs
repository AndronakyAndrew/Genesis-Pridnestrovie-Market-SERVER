using System;
using GenesisMarket.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .Annotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .Annotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .Annotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .Annotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .OldAnnotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .OldAnnotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .OldAnnotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .OldAnnotation("Npgsql:Enum:price_type", "fixed,negotiable,free");

            // SearchVector нельзя «переключить» в GENERATED через ALTER COLUMN — Postgres
            // это не умеет. Пересоздаём колонку сырым SQL: сбрасываем GIN-индекс (он висит
            // на старой колонке), удаляем колонку, добавляем как STORED generated и заново
            // строим GIN. Данных не теряем: колонка производная (считается из Title/Description).
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_listings_SearchVector\";");
            migrationBuilder.Sql("ALTER TABLE listings DROP COLUMN \"SearchVector\";");
            migrationBuilder.Sql(
                "ALTER TABLE listings ADD COLUMN \"SearchVector\" tsvector NOT NULL " +
                $"GENERATED ALWAYS AS ({ListingConfiguration.SearchVectorSql}) STORED;");
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_listings_SearchVector\" ON listings USING gin (\"SearchVector\");");

            migrationBuilder.CreateTable(
                name: "search_misses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Query = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_misses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_misses_CreatedAt",
                table: "search_misses",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_search_misses_Query",
                table: "search_misses",
                column: "Query");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_misses");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .Annotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .Annotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .Annotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .Annotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .OldAnnotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .OldAnnotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .OldAnnotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .OldAnnotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .OldAnnotation("Npgsql:Enum:price_type", "fixed,negotiable,free")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            // Возврат к обычной (не generated) nullable-колонке + GIN.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_listings_SearchVector\";");
            migrationBuilder.Sql("ALTER TABLE listings DROP COLUMN \"SearchVector\";");
            migrationBuilder.Sql("ALTER TABLE listings ADD COLUMN \"SearchVector\" tsvector;");
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_listings_SearchVector\" ON listings USING gin (\"SearchVector\");");
        }
    }
}
