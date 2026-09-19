using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Бизнес-аккаунт как режим пользователя: таблица business_profiles (1:1 с users).
    /// Только добавление — существующие строки users не трогаются: у кого нет
    /// строки в business_profiles, тот частный.
    /// </summary>
    public partial class AddBusinessAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_profiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShopName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LegalForm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RegistrationNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PickupAddress = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RejectionCount = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_profiles", x => x.UserId);
                    table.CheckConstraint("ck_business_profiles_decision_attributed", "\"Status\" NOT IN ('Verified', 'Rejected') OR (\"ReviewedAt\" IS NOT NULL AND \"ReviewedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_business_profiles_pending_submitted", "\"Status\" <> 'Pending' OR \"SubmittedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_business_profiles_rejection_reason", "\"Status\" <> 'Rejected' OR \"RejectionReason\" IS NOT NULL");
                    table.CheckConstraint("ck_business_profiles_status_requires_business", "\"Status\" IN ('None', 'Rejected') OR \"AccountType\" = 'Business'");
                    table.ForeignKey(
                        name: "FK_business_profiles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_profiles_Status_SubmittedAt",
                table: "business_profiles",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_business_profiles_verified_registration_number",
                table: "business_profiles",
                column: "RegistrationNumber",
                unique: true,
                filter: "\"Status\" = 'Verified'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_profiles");
        }
    }
}
