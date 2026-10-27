using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvitedStoreAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RegistrationNumber",
                table: "business_profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "PickupAddress",
                table: "business_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "LegalForm",
                table: "business_profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<bool>(
                name: "IsInvitedStore",
                table: "business_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShopCity",
                table: "business_profiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Accepted invitations are retained by the cleanup job. If a user accepted
            // several, the first accepted storefront is selected deterministically.
            // Existing business profiles keep their name, city (when present), and every
            // legal and moderation field; only their durable store marker is added.
            migrationBuilder.Sql("""
                WITH accepted AS (
                    SELECT DISTINCT ON ("AcceptedByUserId")
                        "AcceptedByUserId" AS "UserId", "ShopName", "City", "AcceptedAt"
                    FROM seller_invitations
                    WHERE "Status" = 'Accepted' AND "AcceptedByUserId" IS NOT NULL
                    ORDER BY "AcceptedByUserId", "AcceptedAt", "Id"
                )
                UPDATE business_profiles AS bp
                SET "AccountType" = 'Business',
                    "IsInvitedStore" = TRUE,
                    "ShopCity" = COALESCE(bp."ShopCity", accepted."City")
                FROM accepted
                WHERE bp."UserId" = accepted."UserId";

                WITH accepted AS (
                    SELECT DISTINCT ON ("AcceptedByUserId")
                        "AcceptedByUserId" AS "UserId", "ShopName", "City", "AcceptedAt"
                    FROM seller_invitations
                    WHERE "Status" = 'Accepted' AND "AcceptedByUserId" IS NOT NULL
                    ORDER BY "AcceptedByUserId", "AcceptedAt", "Id"
                )
                INSERT INTO business_profiles
                    ("UserId", "AccountType", "ShopName", "ShopCity", "IsInvitedStore",
                     "Status", "RejectionCount", "CreatedAt")
                SELECT accepted."UserId", 'Business', accepted."ShopName", accepted."City", TRUE,
                       'None', 0, COALESCE(accepted."AcceptedAt", now())
                FROM accepted
                WHERE NOT EXISTS (
                    SELECT 1 FROM business_profiles bp WHERE bp."UserId" = accepted."UserId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_business_profiles_ShopCity",
                table: "business_profiles",
                column: "ShopCity");

            migrationBuilder.AddForeignKey(
                name: "FK_business_profiles_cities_ShopCity",
                table: "business_profiles",
                column: "ShopCity",
                principalTable: "cities",
                principalColumn: "Code",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_business_profiles_cities_ShopCity",
                table: "business_profiles");

            migrationBuilder.DropIndex(
                name: "IX_business_profiles_ShopCity",
                table: "business_profiles");

            migrationBuilder.DropColumn(
                name: "IsInvitedStore",
                table: "business_profiles");

            migrationBuilder.DropColumn(
                name: "ShopCity",
                table: "business_profiles");

            migrationBuilder.AlterColumn<string>(
                name: "RegistrationNumber",
                table: "business_profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PickupAddress",
                table: "business_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LegalForm",
                table: "business_profiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);
        }
    }
}
