using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_ProcessedAt_CreatedAt",
                table: "outbox_messages");

            migrationBuilder.RenameColumn(
                name: "LastError",
                table: "outbox_messages",
                newName: "Error");

            migrationBuilder.AddColumn<string>(
                name: "NotifyVia",
                table: "profiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Email");

            migrationBuilder.AddColumn<long>(
                name: "TelegramChatId",
                table: "profiles",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Payload",
                table: "outbox_messages",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "outbox_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Pending");

            // ---- Бэкфилл ранее записанных сообщений ----
            // Время следующей попытки выравниваем на момент создания (FIFO сохраняется).
            migrationBuilder.Sql(
                "UPDATE outbox_messages SET \"NextAttemptAt\" = \"CreatedAt\";");
            // Уже обработанные (ProcessedAt задан) и legacy-уведомления (тип 'notification',
            // для которого обработчика больше нет) считаем доставленными: не гоняем их заново
            // и не сыпем ошибками. Непроцессенные 'delete-object' остаются Pending — у них
            // есть совместимый обработчик.
            migrationBuilder.Sql(
                "UPDATE outbox_messages " +
                "SET \"Status\" = 'Done', \"ProcessedAt\" = COALESCE(\"ProcessedAt\", now()) " +
                "WHERE \"ProcessedAt\" IS NOT NULL OR \"Type\" = 'notification';");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt",
                table: "outbox_messages",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_due",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" },
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_ProcessedAt",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_due",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "NotifyVia",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "TelegramChatId",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "outbox_messages");

            migrationBuilder.RenameColumn(
                name: "Error",
                table: "outbox_messages",
                newName: "LastError");

            migrationBuilder.AlterColumn<string>(
                name: "Payload",
                table: "outbox_messages",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt_CreatedAt",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "CreatedAt" });
        }
    }
}
