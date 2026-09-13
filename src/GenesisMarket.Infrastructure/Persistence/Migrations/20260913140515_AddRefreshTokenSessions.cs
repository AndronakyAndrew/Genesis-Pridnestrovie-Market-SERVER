using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Сессии поверх refresh-токенов (вкладка «Безопасность»).
    ///
    /// SessionId и SessionStartedAt добавляются в три шага. Сгенерированный по
    /// умолчанию вариант (сразу NOT NULL с defaultValue) на непустой таблице
    /// испортил бы данные, а не упал: всем существующим строкам достался бы
    /// SessionId «00000000-…», то есть ВСЕ прежние сессии пользователя слились бы
    /// в одну — и отзыв одной отозвал бы их скопом. SessionStartedAt при этом
    /// уехал бы в 0001-01-01 и показывался бы в интерфейсе как есть.
    ///
    /// Поэтому: колонки появляются nullable, существующие строки заполняются
    /// осмысленно (каждый живой токен = отдельная сессия, начатая когда он выдан),
    /// и только потом ставится NOT NULL.
    /// </summary>
    public partial class AddRefreshTokenSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserFamily",
                table: "refresh_tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceFamily",
                table: "refresh_tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpPrefix",
                table: "refresh_tokens",
                type: "character varying(39)",
                maxLength: 39,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsFamily",
                table: "refresh_tokens",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Шаг 1: обе колонки — nullable, существующие строки не трогаем.
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionStartedAt",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            // Шаг 2: заполняем уже выданные токены. Каждый существующий токен —
            // отдельная сессия (цепочек ротации до этой миграции мы не знаем),
            // её начало — момент выдачи токена.
            migrationBuilder.Sql("""
                UPDATE refresh_tokens
                SET "SessionId"        = COALESCE("SessionId", "Id"),
                    "SessionStartedAt" = COALESCE("SessionStartedAt", "CreatedAt");
                """);

            // Шаг 3: пустых значений не осталось — можно требовать NOT NULL.
            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "SessionStartedAt",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId_SessionId",
                table: "refresh_tokens",
                columns: new[] { "UserId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_UserId_SessionId",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "BrowserFamily",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "DeviceFamily",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "IpPrefix",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "OsFamily",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "SessionStartedAt",
                table: "refresh_tokens");
        }
    }
}
