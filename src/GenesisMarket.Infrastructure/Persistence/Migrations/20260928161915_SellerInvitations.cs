using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SellerInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasPassword",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreparedByUserId",
                table: "listings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceInvitationId",
                table: "listings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "publication_consents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_consents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "seller_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    City = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InternalNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    LinkIssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LinkVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_invitations", x => x.Id);
                    table.CheckConstraint("ck_seller_invitations_accepted", "(\"Status\" = 'Accepted') = (\"AcceptedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_seller_invitations_shop_name", "char_length(btrim(\"ShopName\")) BETWEEN 2 AND 60");
                    table.CheckConstraint("ck_seller_invitations_token", "(\"Status\" IN ('Pending', 'Expired')) = (\"TokenHash\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_seller_invitations_categories_Category",
                        column: x => x.Category,
                        principalTable: "categories",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_invitations_cities_City",
                        column: x => x.City,
                        principalTable: "cities",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invitation_drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    PriceType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,0)", nullable: true),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubcategoryId = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_drafts", x => x.Id);
                    table.CheckConstraint("ck_invitation_drafts_price", "(\"PriceType\" = 'Negotiable' AND \"Price\" IS NULL) OR (\"PriceType\" = 'Free' AND \"Price\" = 0) OR (\"PriceType\" = 'Fixed' AND \"Price\" IS NOT NULL AND \"Price\" >= 0)");
                    table.CheckConstraint("ck_invitation_drafts_title", "char_length(\"Title\") BETWEEN 5 AND 120");
                    table.ForeignKey(
                        name: "FK_invitation_drafts_categories_Category",
                        column: x => x.Category,
                        principalTable: "categories",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invitation_drafts_seller_invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "seller_invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitation_drafts_subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invitation_draft_photos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ThumbKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_draft_photos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invitation_draft_photos_invitation_drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "invitation_drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_listings_SourceInvitationId",
                table: "listings",
                column: "SourceInvitationId",
                filter: "\"SourceInvitationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invitation_draft_photos_DraftId_SortOrder",
                table: "invitation_draft_photos",
                columns: new[] { "DraftId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_invitation_drafts_Category",
                table: "invitation_drafts",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_invitation_drafts_InvitationId_SortOrder",
                table: "invitation_drafts",
                columns: new[] { "InvitationId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_invitation_drafts_SubcategoryId",
                table: "invitation_drafts",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_publication_consents_ListingId_Version",
                table: "publication_consents",
                columns: new[] { "ListingId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_consents_UserId",
                table: "publication_consents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_invitations_Category",
                table: "seller_invitations",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_seller_invitations_City",
                table: "seller_invitations",
                column: "City");

            migrationBuilder.CreateIndex(
                name: "IX_seller_invitations_CreatedByUserId_CreatedAt",
                table: "seller_invitations",
                columns: new[] { "CreatedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_invitations_Status_StatusChangedAt",
                table: "seller_invitations",
                columns: new[] { "Status", "StatusChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_invitations_TokenHash",
                table: "seller_invitations",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_listings_seller_invitations_SourceInvitationId",
                table: "listings",
                column: "SourceInvitationId",
                principalTable: "seller_invitations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Согласие на публикацию — юридический след: только на добавление, как журнал действий.
            // Единственная разрешённая правка — снятие IpHash по сроку хранения (JournalRetention).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION journal_append_only() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND TG_TABLE_NAME IN ('audit_events', 'publication_consents')
                       AND NEW."IpHash" IS NULL
                       AND (to_jsonb(NEW) - 'IpHash') = (to_jsonb(OLD) - 'IpHash') THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'Таблица % только на добавление: % запрещён', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_publication_consents_append_only
                    BEFORE UPDATE OR DELETE ON publication_consents
                    FOR EACH ROW EXECUTE FUNCTION journal_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_publication_consents_append_only ON publication_consents;

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
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_listings_seller_invitations_SourceInvitationId",
                table: "listings");

            migrationBuilder.DropTable(
                name: "invitation_draft_photos");

            migrationBuilder.DropTable(
                name: "publication_consents");

            migrationBuilder.DropTable(
                name: "invitation_drafts");

            migrationBuilder.DropTable(
                name: "seller_invitations");

            migrationBuilder.DropIndex(
                name: "IX_listings_SourceInvitationId",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "HasPassword",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PreparedByUserId",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "SourceInvitationId",
                table: "listings");
        }
    }
}
