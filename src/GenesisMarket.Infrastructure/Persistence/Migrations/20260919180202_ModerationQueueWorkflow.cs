using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModerationQueueWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewAssignedAt",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewAssigneeId",
                table: "listings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevisionRequestedAt",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_listings_ReviewAssigneeId",
                table: "listings",
                column: "ReviewAssigneeId",
                filter: "\"ReviewAssigneeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_RevisionRequestedAt",
                table: "listings",
                column: "RevisionRequestedAt",
                filter: "\"RevisionRequestedAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_listings_ReviewAssigneeId",
                table: "listings");

            migrationBuilder.DropIndex(
                name: "IX_listings_RevisionRequestedAt",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "ReviewAssignedAt",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "ReviewAssigneeId",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "RevisionRequestedAt",
                table: "listings");
        }
    }
}
