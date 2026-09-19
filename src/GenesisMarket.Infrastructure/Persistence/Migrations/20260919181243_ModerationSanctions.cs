using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModerationSanctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastWarnedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WarningsCount",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "blocked_cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocked_cards", x => x.Id);
                    table.CheckConstraint("ck_blocked_cards_last4", "\"Last4\" ~ '^[0-9]{4}$'");
                    table.CheckConstraint("ck_blocked_cards_reason_length", "char_length(\"Reason\") <= 500");
                });

            migrationBuilder.CreateIndex(
                name: "IX_blocked_cards_CardHash",
                table: "blocked_cards",
                column: "CardHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blocked_cards_CreatedAt",
                table: "blocked_cards",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blocked_cards");

            migrationBuilder.DropColumn(
                name: "LastWarnedAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "WarningsCount",
                table: "users");
        }
    }
}
