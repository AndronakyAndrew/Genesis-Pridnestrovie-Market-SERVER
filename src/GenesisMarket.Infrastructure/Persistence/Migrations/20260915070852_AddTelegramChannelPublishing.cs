using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramChannelPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TelegramPublishedAt",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_post_queue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Pending"),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_post_queue", x => x.Id);
                    table.CheckConstraint("ck_channel_post_queue_attempts_nonnegative", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_channel_post_queue_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "link_clicks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_link_clicks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_post_queue_ListingId",
                table: "channel_post_queue",
                column: "ListingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_post_queue_Status_EnqueuedAt",
                table: "channel_post_queue",
                columns: new[] { "Status", "EnqueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_link_clicks_ListingId_CreatedAt",
                table: "link_clicks",
                columns: new[] { "ListingId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_post_queue");

            migrationBuilder.DropTable(
                name: "link_clicks");

            migrationBuilder.DropColumn(
                name: "TelegramPublishedAt",
                table: "listings");
        }
    }
}
