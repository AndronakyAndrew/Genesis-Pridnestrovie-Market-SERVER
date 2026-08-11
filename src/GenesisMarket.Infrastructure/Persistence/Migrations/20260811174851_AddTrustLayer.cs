using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrustLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AverageRating",
                table: "users",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewsCount",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ModerationPriority",
                table: "listings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReporterIpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "New"),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.Id);
                    table.CheckConstraint("ck_reports_comment_length", "\"Comment\" IS NULL OR char_length(\"Comment\") <= 500");
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    HiddenByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.Id);
                    table.CheckConstraint("ck_reviews_rating_range", "\"Rating\" >= 1 AND \"Rating\" <= 5");
                    table.CheckConstraint("ck_reviews_text_length", "char_length(\"Text\") <= 1000");
                    table.ForeignKey(
                        name: "FK_reviews_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reviews_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reviews_users_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reports_ReporterId_TargetType_TargetId",
                table: "reports",
                columns: new[] { "ReporterId", "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_reports_Status_CreatedAt",
                table: "reports",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reports_TargetType_TargetId",
                table: "reports",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_AuthorId_ListingId",
                table: "reviews",
                columns: new[] { "AuthorId", "ListingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_ListingId",
                table: "reviews",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_TargetUserId_CreatedAt_Id",
                table: "reviews",
                columns: new[] { "TargetUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"IsHidden\" = false");

            // Денормализованный агрегат рейтинга продавца (users.AverageRating/ReviewsCount)
            // пересчитывается триггером в той же транзакции, что и запись/редактирование/
            // скрытие отзыва — не AVG/COUNT на каждый запрос профиля. Скрытые (IsHidden)
            // отзывы в агрегат не входят. AverageRating = NULL, когда видимых отзывов нет.
            migrationBuilder.Sql(
                "CREATE OR REPLACE FUNCTION reviews_rating_sync() RETURNS trigger AS $$\n" +
                "DECLARE\n" +
                "    target uuid := COALESCE(NEW.\"TargetUserId\", OLD.\"TargetUserId\");\n" +
                "BEGIN\n" +
                "    UPDATE users u SET\n" +
                "        \"ReviewsCount\" = agg.cnt,\n" +
                "        \"AverageRating\" = agg.avg\n" +
                "    FROM (\n" +
                "        SELECT\n" +
                "            COUNT(*)::int AS cnt,\n" +
                "            CASE WHEN COUNT(*) = 0 THEN NULL\n" +
                "                 ELSE round(avg(\"Rating\")::numeric, 2)::double precision END AS avg\n" +
                "        FROM reviews\n" +
                "        WHERE \"TargetUserId\" = target AND \"IsHidden\" = false\n" +
                "    ) agg\n" +
                "    WHERE u.\"Id\" = target;\n" +
                "    RETURN NULL;\n" +
                "END;\n" +
                "$$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(
                "CREATE TRIGGER trg_reviews_rating " +
                "AFTER INSERT OR UPDATE OR DELETE ON reviews " +
                "FOR EACH ROW EXECUTE FUNCTION reviews_rating_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_reviews_rating ON reviews;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS reviews_rating_sync();");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropColumn(
                name: "AverageRating",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ReviewsCount",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ModerationPriority",
                table: "listings");
        }
    }
}
