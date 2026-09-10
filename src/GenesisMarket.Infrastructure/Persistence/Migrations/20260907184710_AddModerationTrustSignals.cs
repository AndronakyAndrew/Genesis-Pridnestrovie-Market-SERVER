using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationTrustSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovedListingsCount",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRejectedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewQueuedAt",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_listings_ModerationPriority_ReviewQueuedAt",
                table: "listings",
                columns: new[] { "ModerationPriority", "ReviewQueuedAt" },
                descending: new[] { true, false },
                filter: "\"ReviewQueuedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_OwnerId_ApprovedAt",
                table: "listings",
                columns: new[] { "OwnerId", "ApprovedAt" },
                filter: "\"ApprovedAt\" IS NOT NULL");

            // ---- Бэкфилл по уже существующим данным ----

            // Исторического ApprovedAt нет. Ближайшее верное приближение «объявление было
            // проверенно-живым» — оно дошло до каталога: active/sold/archived с PublishedAt.
            // pendingreview и rejected сюда намеренно не попадают: они одобрения не проходили.
            migrationBuilder.Sql(
                "UPDATE listings SET \"ApprovedAt\" = \"PublishedAt\" " +
                "WHERE \"PublishedAt\" IS NOT NULL " +
                "  AND \"Status\" IN ('active', 'sold', 'archived');");

            // Всё, что сейчас на премодерации, ставим в очередь задним числом — иначе
            // после деплоя эти объявления пропали бы из очереди модератора.
            migrationBuilder.Sql(
                "UPDATE listings SET \"ReviewQueuedAt\" = COALESCE(\"PublishedAt\", \"CreatedAt\") " +
                "WHERE \"Status\" = 'pendingreview';");

            migrationBuilder.Sql(
                "UPDATE users u SET \"ApprovedListingsCount\" = c.cnt " +
                "FROM (SELECT \"OwnerId\", COUNT(*)::int AS cnt FROM listings " +
                "      WHERE \"ApprovedAt\" IS NOT NULL GROUP BY \"OwnerId\") c " +
                "WHERE u.\"Id\" = c.\"OwnerId\";");

            // Последний отказ восстанавливаем из журнала модерации — он append-only,
            // поэтому история отказов там полная.
            migrationBuilder.Sql(
                "UPDATE users u SET \"LastRejectedAt\" = r.last_rejected " +
                "FROM (SELECT l.\"OwnerId\", MAX(m.\"CreatedAt\") AS last_rejected " +
                "      FROM moderation_logs m " +
                "      JOIN listings l ON l.\"Id\" = m.\"TargetId\" " +
                "      WHERE m.\"Action\" = 'listing.reject' " +
                "      GROUP BY l.\"OwnerId\") r " +
                "WHERE u.\"Id\" = r.\"OwnerId\";");

            // ---- Триггер денормализованных сигналов доверия ----
            // Живёт в БД, а не в прикладном коде, намеренно: статусы объявлений меняют
            // и ExecuteUpdate в контроллерах, и джобы гигиены каталога, и эта же миграция.
            // Триггер ловит все пути разом, забыть его на новом пути нельзя.
            // OLD трогаем только в ветке UPDATE: в INSERT-триггере обращение к его полям
            // — ошибка выполнения, а SQL-OR права короткого замыкания не даёт.
            migrationBuilder.Sql(
                "CREATE OR REPLACE FUNCTION listings_trust_sync() RETURNS trigger AS $$\n" +
                "BEGIN\n" +
                "    IF (TG_OP = 'INSERT') THEN\n" +
                "        -- Автопубликация доверенного автора: строка вставляется уже одобренной.\n" +
                "        IF (NEW.\"ApprovedAt\" IS NOT NULL) THEN\n" +
                "            UPDATE users SET \"ApprovedListingsCount\" = \"ApprovedListingsCount\" + 1\n" +
                "            WHERE \"Id\" = NEW.\"OwnerId\";\n" +
                "        END IF;\n" +
                "    ELSE\n" +
                "        -- Объявление впервые получило ApprovedAt ⇒ +1 к доверию автора.\n" +
                "        IF (NEW.\"ApprovedAt\" IS NOT NULL AND OLD.\"ApprovedAt\" IS NULL) THEN\n" +
                "            UPDATE users SET \"ApprovedListingsCount\" = \"ApprovedListingsCount\" + 1\n" +
                "            WHERE \"Id\" = NEW.\"OwnerId\";\n" +
                "        END IF;\n" +
                "\n" +
                "        -- Переход в rejected ⇒ фиксируем момент последнего отказа автору.\n" +
                "        IF (NEW.\"Status\" = 'rejected' AND OLD.\"Status\" IS DISTINCT FROM 'rejected') THEN\n" +
                "            UPDATE users SET \"LastRejectedAt\" = NEW.\"UpdatedAt\"\n" +
                "            WHERE \"Id\" = NEW.\"OwnerId\";\n" +
                "        END IF;\n" +
                "    END IF;\n" +
                "\n" +
                "    RETURN NULL;\n" +
                "END;\n" +
                "$$ LANGUAGE plpgsql;");

            // Условие WHEN снаружи функции: по listings идут частые UPDATE, не имеющие
            // к доверию отношения (счётчик просмотров, bump, избранное). Без WHEN каждый
            // такой UPDATE вызывал бы plpgsql-функцию впустую.
            migrationBuilder.Sql(
                "CREATE TRIGGER trg_listings_trust_insert " +
                "AFTER INSERT ON listings " +
                "FOR EACH ROW WHEN (NEW.\"ApprovedAt\" IS NOT NULL) " +
                "EXECUTE FUNCTION listings_trust_sync();");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_listings_trust_update " +
                "AFTER UPDATE ON listings " +
                "FOR EACH ROW WHEN (" +
                "(NEW.\"ApprovedAt\" IS NOT NULL AND OLD.\"ApprovedAt\" IS NULL) " +
                "OR (NEW.\"Status\" = 'rejected' AND OLD.\"Status\" IS DISTINCT FROM 'rejected')) " +
                "EXECUTE FUNCTION listings_trust_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_listings_trust_insert ON listings;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_listings_trust_update ON listings;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS listings_trust_sync();");

            migrationBuilder.DropIndex(
                name: "IX_listings_ModerationPriority_ReviewQueuedAt",
                table: "listings");

            migrationBuilder.DropIndex(
                name: "IX_listings_OwnerId_ApprovedAt",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "ApprovedListingsCount",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastRejectedAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "ReviewQueuedAt",
                table: "listings");
        }
    }
}
