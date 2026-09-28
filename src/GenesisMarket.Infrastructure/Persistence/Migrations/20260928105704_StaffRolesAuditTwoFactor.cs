using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffRolesAuditTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaVerifiedAt",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorKind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ActorLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OldValue = table.Column<string>(type: "jsonb", nullable: true),
                    NewValue = table.Column<string>(type: "jsonb", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                    table.CheckConstraint("ck_audit_events_actor_kind", "\"ActorKind\" IN ('user', 'cli', 'system')");
                    table.CheckConstraint("ck_audit_events_user_actor", "\"ActorKind\" <> 'user' OR \"ActorId\" IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "mfa_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mfa_challenges_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_role_assignments", x => x.Id);
                    table.CheckConstraint("ck_project_role_assignments_role", "\"Role\" IN ('Moderator', 'Admin', 'Owner')");
                    table.ForeignKey(
                        name: "FK_project_role_assignments_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NewRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ActorLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_changes", x => x.Id);
                    table.CheckConstraint("ck_role_changes_source", "\"Source\" IN ('ui', 'cli')");
                });

            migrationBuilder.CreateTable(
                name: "user_two_factor",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretCipher = table.Column<byte[]>(type: "bytea", nullable: false),
                    EnabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedTimeStep = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_two_factor", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_user_two_factor_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recovery_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_recovery_codes_user_two_factor_UserId",
                        column: x => x.UserId,
                        principalTable: "user_two_factor",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Action",
                table: "audit_events",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ActorId_OccurredAt",
                table: "audit_events",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_OccurredAt_Id",
                table: "audit_events",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TargetType_TargetId",
                table: "audit_events",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenges_TokenHash",
                table: "mfa_challenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mfa_challenges_UserId",
                table: "mfa_challenges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_project_role_assignments_UserId_ProjectKey",
                table: "project_role_assignments",
                columns: new[] { "UserId", "ProjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_project_role_assignments_single_owner",
                table: "project_role_assignments",
                column: "ProjectKey",
                unique: true,
                filter: "\"Role\" = 'Owner'");

            migrationBuilder.CreateIndex(
                name: "IX_role_changes_OccurredAt",
                table: "role_changes",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_role_changes_UserId_OccurredAt",
                table: "role_changes",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_recovery_codes_UserId_CodeHash",
                table: "user_recovery_codes",
                columns: new[] { "UserId", "CodeHash" },
                unique: true);

            // ---- Перенос ролей: users.Role → project_role_assignments ----
            // Строка — только служебным ролям (User = отсутствие строки). Owner в старой
            // схеме не было: владелец назначается после миграции командой grant-role.
            migrationBuilder.Sql("""
                INSERT INTO project_role_assignments ("Id", "UserId", "ProjectKey", "Role", "GrantedByUserId", "GrantedAt")
                SELECT gen_random_uuid(), u."Id", 'market', u."Role", NULL, now()
                FROM users u
                WHERE u."Role" IN ('Moderator', 'Admin');
                """);

            // Колонка удаляется только после переноса — иначе роли пропали бы.
            migrationBuilder.DropColumn(
                name: "Role",
                table: "users");

            // ---- Журналы только на добавление ----
            // Прикладной код их не меняет, но гарантию даёт БД: UPDATE и DELETE отклоняются
            // триггером, даже если запрос пришёл в обход приложения. Единственное исключение —
            // снятие IpHash в audit_events по сроку хранения (JournalRetention).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION journal_append_only() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND TG_TABLE_NAME = 'audit_events'
                       AND NEW."IpHash" IS NULL
                       AND (to_jsonb(NEW) - 'IpHash') = (to_jsonb(OLD) - 'IpHash') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Таблица % только на добавление: % запрещён', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_audit_events_append_only
                    BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION journal_append_only();

                CREATE TRIGGER trg_role_changes_append_only
                    BEFORE UPDATE OR DELETE ON role_changes
                    FOR EACH ROW EXECUTE FUNCTION journal_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "User");

            // Обратный перенос. Owner в старой схеме нет — он становится Admin.
            migrationBuilder.Sql("""
                UPDATE users u
                SET "Role" = CASE a."Role" WHEN 'Owner' THEN 'Admin' ELSE a."Role" END
                FROM project_role_assignments a
                WHERE a."UserId" = u."Id" AND a."ProjectKey" = 'market';
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_events_append_only ON audit_events;
                DROP TRIGGER IF EXISTS trg_role_changes_append_only ON role_changes;
                DROP FUNCTION IF EXISTS journal_append_only();
                """);

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "mfa_challenges");

            migrationBuilder.DropTable(
                name: "project_role_assignments");

            migrationBuilder.DropTable(
                name: "role_changes");

            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropTable(
                name: "user_two_factor");

            migrationBuilder.DropColumn(
                name: "MfaVerifiedAt",
                table: "refresh_tokens");
        }
    }
}
