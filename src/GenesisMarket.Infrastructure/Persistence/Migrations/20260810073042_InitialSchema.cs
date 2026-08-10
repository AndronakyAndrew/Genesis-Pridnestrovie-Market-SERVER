using System;
using GenesisMarket.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GenesisMarket.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:category", "realestate,transport,electronics,home,fashion,kids,work,services,animals,other")
                .Annotation("Npgsql:Enum:city", "tiraspol,bendery,rybnitsa,dubossary,slobodzea,grigoriopol,dnestrovsk")
                .Annotation("Npgsql:Enum:condition", "new,used,notapplicable")
                .Annotation("Npgsql:Enum:listing_status", "draft,pendingreview,active,sold,archived,rejected")
                .Annotation("Npgsql:Enum:price_type", "fixed,negotiable,free");

            migrationBuilder.CreateTable(
                name: "subcategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<Category>(type: "category", nullable: false),
                    Slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subcategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "User"),
                    PhoneE164 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PhoneVerified = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SecurityStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    IsBanned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    BannedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "listings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(12,0)", nullable: true),
                    PriceType = table.Column<PriceType>(type: "price_type", nullable: false),
                    Category = table.Column<Category>(type: "category", nullable: false),
                    SubcategoryId = table.Column<int>(type: "integer", nullable: false),
                    City = table.Column<City>(type: "city", nullable: false),
                    District = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Condition = table.Column<Condition>(type: "condition", nullable: false),
                    Status = table.Column<ListingStatus>(type: "listing_status", nullable: false),
                    ViewsCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listings", x => x.Id);
                    table.CheckConstraint("ck_listings_description_length", "char_length(\"Description\") <= 5000");
                    table.CheckConstraint("ck_listings_district_length", "\"District\" IS NULL OR char_length(\"District\") <= 100");
                    table.CheckConstraint("ck_listings_price_nonnegative", "\"Price\" IS NULL OR \"Price\" >= 0");
                    table.CheckConstraint("ck_listings_price_pricetype", "(\"PriceType\" = 'free' AND \"Price\" = 0) OR (\"PriceType\" = 'negotiable' AND \"Price\" IS NULL) OR (\"PriceType\" = 'fixed' AND \"Price\" IS NOT NULL AND \"Price\" >= 0)");
                    table.CheckConstraint("ck_listings_title_length", "char_length(\"Title\") >= 5 AND char_length(\"Title\") <= 120");
                    table.CheckConstraint("ck_listings_views_nonnegative", "\"ViewsCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_listings_subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_listings_users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    City = table.Column<City>(type: "city", nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TelegramUsername = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ViberEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    WhatsappEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ShowPhoneInListing = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.UserId);
                    table.CheckConstraint("ck_profiles_display_name_length", "char_length(\"DisplayName\") <= 60");
                    table.ForeignKey(
                        name: "FK_profiles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversations_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conversations_users_BuyerId",
                        column: x => x.BuyerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conversations_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "favorites",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_favorites", x => new { x.UserId, x.ListingId });
                    table.ForeignKey(
                        name: "FK_favorites_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_favorites_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "listing_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ThumbKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listing_images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_listing_images_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.Id);
                    table.CheckConstraint("ck_messages_text_length", "char_length(\"Text\") <= 2000");
                    table.ForeignKey(
                        name: "FK_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_messages_users_SenderId",
                        column: x => x.SenderId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "subcategories",
                columns: new[] { "Id", "Category", "Name", "Slug" },
                values: new object[,]
                {
                    { 1, Category.RealEstate, "Квартиры", "kvartiry" },
                    { 2, Category.RealEstate, "Дома, дачи, коттеджи", "doma" },
                    { 3, Category.RealEstate, "Комнаты", "komnaty" },
                    { 4, Category.RealEstate, "Земельные участки", "zemelnye-uchastki" },
                    { 5, Category.RealEstate, "Гаражи и машиноместа", "garazhi" },
                    { 6, Category.RealEstate, "Коммерческая недвижимость", "kommercheskaya" },
                    { 7, Category.Transport, "Легковые автомобили", "legkovye-avto" },
                    { 8, Category.Transport, "Грузовики и спецтехника", "gruzoviki" },
                    { 9, Category.Transport, "Мотоциклы и мототехника", "mototsikly" },
                    { 10, Category.Transport, "Запчасти и аксессуары", "zapchasti" },
                    { 11, Category.Transport, "Водный транспорт", "vodnyy-transport" },
                    { 12, Category.Electronics, "Телефоны", "telefony" },
                    { 13, Category.Electronics, "Ноутбуки", "noutbuki" },
                    { 14, Category.Electronics, "Компьютеры и комплектующие", "kompyutery" },
                    { 15, Category.Electronics, "Телевизоры и проекторы", "tv" },
                    { 16, Category.Electronics, "Фото и видео", "foto-video" },
                    { 17, Category.Electronics, "Аудиотехника", "audio" },
                    { 18, Category.Home, "Мебель", "mebel" },
                    { 19, Category.Home, "Бытовая техника", "bytovaya-tehnika" },
                    { 20, Category.Home, "Ремонт и стройка", "remont-stroyka" },
                    { 21, Category.Home, "Сад и огород", "sad-ogorod" },
                    { 22, Category.Home, "Посуда и товары для дома", "posuda" },
                    { 23, Category.Fashion, "Мужская одежда", "muzhskaya-odezhda" },
                    { 24, Category.Fashion, "Женская одежда", "zhenskaya-odezhda" },
                    { 25, Category.Fashion, "Обувь", "obuv" },
                    { 26, Category.Fashion, "Аксессуары", "aksessuary" },
                    { 27, Category.Kids, "Детская одежда и обувь", "detskaya-odezhda" },
                    { 28, Category.Kids, "Игрушки", "igrushki" },
                    { 29, Category.Kids, "Коляски", "kolyaski" },
                    { 30, Category.Kids, "Детская мебель", "detskaya-mebel" },
                    { 31, Category.Work, "Вакансии", "vakansii" },
                    { 32, Category.Work, "Резюме", "rezume" },
                    { 33, Category.Services, "Строительство и ремонт", "stroitelstvo-remont" },
                    { 34, Category.Services, "Красота и здоровье", "krasota-zdorovie" },
                    { 35, Category.Services, "Обучение и курсы", "obuchenie" },
                    { 36, Category.Services, "Перевозки и грузчики", "perevozki" },
                    { 37, Category.Services, "Ремонт техники", "remont-tehniki" },
                    { 38, Category.Animals, "Собаки", "sobaki" },
                    { 39, Category.Animals, "Кошки", "koshki" },
                    { 40, Category.Animals, "Птицы", "ptitsy" },
                    { 41, Category.Animals, "Товары для животных", "tovary-dlya-zhivotnyh" },
                    { 42, Category.Other, "Разное", "raznoe" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_BuyerId",
                table: "conversations",
                column: "BuyerId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_ListingId_BuyerId",
                table: "conversations",
                columns: new[] { "ListingId", "BuyerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_SellerId",
                table: "conversations",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_favorites_ListingId",
                table: "favorites",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_listing_images_ListingId_SortOrder",
                table: "listing_images",
                columns: new[] { "ListingId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_Category_City_Status",
                table: "listings",
                columns: new[] { "Category", "City", "Status" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_OwnerId_Status",
                table: "listings",
                columns: new[] { "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_SearchVector",
                table: "listings",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_listings_Status_CreatedAt",
                table: "listings",
                columns: new[] { "Status", "CreatedAt" },
                descending: new[] { false, true },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_listings_SubcategoryId",
                table: "listings",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId_CreatedAt",
                table: "messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_SenderId",
                table: "messages",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "IX_subcategories_Category_Slug",
                table: "subcategories",
                columns: new[] { "Category", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "favorites");

            migrationBuilder.DropTable(
                name: "listing_images");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "profiles");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropTable(
                name: "listings");

            migrationBuilder.DropTable(
                name: "subcategories");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
