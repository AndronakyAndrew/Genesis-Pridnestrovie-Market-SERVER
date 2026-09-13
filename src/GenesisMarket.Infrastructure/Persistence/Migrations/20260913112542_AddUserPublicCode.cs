using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Публичный номер аккаунта. Накатывается в три шага: сгенерированный
    /// по умолчанию вариант (сразу NOT NULL с defaultValue "" плюс уникальный
    /// индекс) на непустой таблице падает — все существующие строки получают
    /// один и тот же пустой код и первая же пара ломает уникальность.
    /// </summary>
    public partial class AddUserPublicCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Шаг 1. Колонка появляется nullable — существующие строки не трогаем.
            migrationBuilder.AddColumn<string>(
                name: "PublicCode",
                table: "users",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            // Шаг 2. Раздаём коды уже заведённым пользователям.
            //
            // Берём весь диапазон 10000–99999, перемешиваем его (row_number по
            // random()) и раздаём по одному коду на пользователя. Перестановка,
            // а не случайный выбор на строку: так уникальность гарантирована
            // конструкцией, без циклов и повторных попыток внутри миграции.
            // Порядок раздачи не связан с порядком регистрации — соседние по
            // дате аккаунты получают несоседние коды.
            //
            // Если пользователей окажется больше 90 000, часть строк останется
            // с NULL и шаг 3 упадёт. Это сознательно: миграция должна остановиться
            // и потребовать расширения разрядности, а не выдать дубли.
            migrationBuilder.Sql("""
                WITH pool AS (
                    SELECT g::text AS code,
                           row_number() OVER (ORDER BY random()) AS rn
                    FROM generate_series(10000, 99999) AS g
                ),
                targets AS (
                    SELECT "Id",
                           row_number() OVER (ORDER BY "Id") AS rn
                    FROM users
                    WHERE "PublicCode" IS NULL
                )
                UPDATE users u
                SET "PublicCode" = p.code
                FROM targets t
                JOIN pool p ON p.rn = t.rn
                WHERE u."Id" = t."Id";
                """);

            // Шаг 3. Теперь пустых значений нет — можно требовать NOT NULL
            // и вешать уникальный индекс.
            migrationBuilder.AlterColumn<string>(
                name: "PublicCode",
                table: "users",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(5)",
                oldMaxLength: 5,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_PublicCode",
                table: "users",
                column: "PublicCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_PublicCode",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PublicCode",
                table: "users");
        }
    }
}
