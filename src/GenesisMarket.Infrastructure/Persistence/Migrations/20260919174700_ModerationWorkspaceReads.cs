using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModerationWorkspaceReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                table: "reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedToUserId",
                table: "reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitSeconds",
                table: "moderation_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_reports_AssignedToUserId_Status",
                table: "reports",
                columns: new[] { "AssignedToUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_CreatedByIpHash",
                table: "refresh_tokens",
                column: "CreatedByIpHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reports_AssignedToUserId_Status",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_CreatedByIpHash",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "WaitSeconds",
                table: "moderation_logs");
        }
    }
}
