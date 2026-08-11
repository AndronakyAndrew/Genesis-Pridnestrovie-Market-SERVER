using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FavoritesCount",
                table: "listings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_listings_favorites_nonnegative",
                table: "listings",
                sql: "\"FavoritesCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_favorites_UserId_CreatedAt_ListingId",
                table: "favorites",
                columns: new[] { "UserId", "CreatedAt", "ListingId" },
                descending: new[] { false, true, true });

            // Бэкфилл денормализованного счётчика по уже существующим записям избранного.
            migrationBuilder.Sql(
                "UPDATE listings l SET \"FavoritesCount\" = c.cnt " +
                "FROM (SELECT \"ListingId\", COUNT(*) AS cnt FROM favorites GROUP BY \"ListingId\") c " +
                "WHERE l.\"Id\" = c.\"ListingId\";");

            // Триггер поддерживает listings.FavoritesCount в той же транзакции, что и
            // вставка/удаление строки favorites — счётчик не считается COUNT'ом на каждый
            // запрос каталога. Идемпотентный POST не вставляет строку ⇒ триггер не срабатывает.
            migrationBuilder.Sql(
                "CREATE OR REPLACE FUNCTION favorites_count_sync() RETURNS trigger AS $$\n" +
                "BEGIN\n" +
                "    IF (TG_OP = 'INSERT') THEN\n" +
                "        UPDATE listings SET \"FavoritesCount\" = \"FavoritesCount\" + 1 WHERE \"Id\" = NEW.\"ListingId\";\n" +
                "    ELSIF (TG_OP = 'DELETE') THEN\n" +
                "        UPDATE listings SET \"FavoritesCount\" = \"FavoritesCount\" - 1 WHERE \"Id\" = OLD.\"ListingId\";\n" +
                "    END IF;\n" +
                "    RETURN NULL;\n" +
                "END;\n" +
                "$$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_favorites_count " +
                "AFTER INSERT OR DELETE ON favorites " +
                "FOR EACH ROW EXECUTE FUNCTION favorites_count_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_favorites_count ON favorites;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS favorites_count_sync();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_listings_favorites_nonnegative",
                table: "listings");

            migrationBuilder.DropIndex(
                name: "IX_favorites_UserId_CreatedAt_ListingId",
                table: "favorites");

            migrationBuilder.DropColumn(
                name: "FavoritesCount",
                table: "listings");
        }
    }
}
