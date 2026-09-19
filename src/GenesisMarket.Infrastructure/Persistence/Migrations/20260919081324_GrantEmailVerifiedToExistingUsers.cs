using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Амнистия перед введением обязательного подтверждения почты: всем существующим
    /// аккаунтам ставим EmailVerified = true.
    ///
    /// Схему не меняет — это перенос данных. Нужен потому, что с этого момента
    /// неподтверждённая почта закрывает раскрытие контактов, и без амнистии правило
    /// задним числом ударило бы по тем, кто регистрировался, когда подтверждение было
    /// необязательным. Новые регистрации проходят подтверждение штатно: код уходит
    /// сразу при регистрации (см. AuthController.Register).
    /// </summary>
    public partial class GrantEmailVerifiedToExistingUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                UPDATE users
                SET "EmailVerified" = TRUE
                WHERE "EmailVerified" = FALSE;
                """);

        /// <summary>
        /// Откат намеренно пустой: какие аккаунты были неподтверждёнными ДО амнистии,
        /// после неё уже не восстановить, а снимать флаг у всех подряд — значит закрыть
        /// контакты и публикацию тем, кто честно подтвердил почту потом.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
