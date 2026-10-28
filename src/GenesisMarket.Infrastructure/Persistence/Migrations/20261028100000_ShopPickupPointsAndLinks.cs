using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShopPickupPointsAndLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConditionsNote",
                table: "business_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instagram",
                table: "business_profiles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "business_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shop_pickup_points",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    City = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsMain = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Hours = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shop_pickup_points", x => x.Id);
                    table.CheckConstraint("ck_shop_pickup_points_address", "char_length(btrim(\"Address\")) BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_shop_pickup_points_main_active", "NOT \"IsMain\" OR \"IsActive\"");
                    table.CheckConstraint("ck_shop_pickup_points_note", "\"Note\" IS NULL OR char_length(\"Note\") <= 200");
                    table.CheckConstraint("ck_shop_pickup_points_sort_order", "\"SortOrder\" >= 0");
                    table.ForeignKey(
                        name: "FK_shop_pickup_points_business_profiles_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "business_profiles",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shop_pickup_points_cities_City",
                        column: x => x.City,
                        principalTable: "cities",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shop_pickup_points_City",
                table: "shop_pickup_points",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "ix_shop_pickup_points_owner_sort",
                table: "shop_pickup_points",
                columns: new[] { "OwnerId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "ux_shop_pickup_points_one_main",
                table: "shop_pickup_points",
                column: "OwnerId",
                unique: true,
                filter: "\"IsMain\" = TRUE");

            migrationBuilder.Sql(ShopPickupPointBackfillSql.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shop_pickup_points");

            migrationBuilder.DropColumn(
                name: "ConditionsNote",
                table: "business_profiles");

            migrationBuilder.DropColumn(
                name: "Instagram",
                table: "business_profiles");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "business_profiles");
        }
    }
}
