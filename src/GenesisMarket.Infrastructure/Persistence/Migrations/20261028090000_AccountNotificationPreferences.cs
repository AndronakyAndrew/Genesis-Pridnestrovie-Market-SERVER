using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261028090000_AccountNotificationPreferences")]
public partial class AccountNotificationPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "notification_preferences",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                GroupKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_preferences", x => new { x.UserId, x.GroupKey });
                table.ForeignKey(
                    name: "FK_notification_preferences_users_UserId",
                    column: x => x.UserId,
                    principalTable: "users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "notification_preferences");
}
