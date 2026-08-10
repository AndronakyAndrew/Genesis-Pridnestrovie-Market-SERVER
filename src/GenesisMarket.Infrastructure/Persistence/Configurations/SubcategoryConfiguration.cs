using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class SubcategoryConfiguration : IEntityTypeConfiguration<Subcategory>
{
    public void Configure(EntityTypeBuilder<Subcategory> b)
    {
        b.ToTable("subcategories");
        b.HasKey(s => s.Id);

        // Стабильные Id из сида → без автоинкремента.
        b.Property(s => s.Id).ValueGeneratedNever();

        // Category — native enum-тип.
        b.Property(s => s.Category).IsRequired();

        b.Property(s => s.Slug).HasMaxLength(60).IsRequired();
        b.Property(s => s.Name).HasMaxLength(60).IsRequired();

        // Slug уникален в пределах категории.
        b.HasIndex(s => new { s.Category, s.Slug }).IsUnique();

        // ВНИМАНИЕ: провизорный сид. Источник правды (pmr_market_prompt.md,
        // раздел CATEGORIES) в репозитории отсутствует — заменить на реальный
        // список подкатегорий, когда файл появится. Id менять нельзя (FK из listings).
        b.HasData(
            // realestate
            New(1, Category.RealEstate, "kvartiry", "Квартиры"),
            New(2, Category.RealEstate, "doma", "Дома, дачи, коттеджи"),
            New(3, Category.RealEstate, "komnaty", "Комнаты"),
            New(4, Category.RealEstate, "zemelnye-uchastki", "Земельные участки"),
            New(5, Category.RealEstate, "garazhi", "Гаражи и машиноместа"),
            New(6, Category.RealEstate, "kommercheskaya", "Коммерческая недвижимость"),
            // transport
            New(7, Category.Transport, "legkovye-avto", "Легковые автомобили"),
            New(8, Category.Transport, "gruzoviki", "Грузовики и спецтехника"),
            New(9, Category.Transport, "mototsikly", "Мотоциклы и мототехника"),
            New(10, Category.Transport, "zapchasti", "Запчасти и аксессуары"),
            New(11, Category.Transport, "vodnyy-transport", "Водный транспорт"),
            // electronics
            New(12, Category.Electronics, "telefony", "Телефоны"),
            New(13, Category.Electronics, "noutbuki", "Ноутбуки"),
            New(14, Category.Electronics, "kompyutery", "Компьютеры и комплектующие"),
            New(15, Category.Electronics, "tv", "Телевизоры и проекторы"),
            New(16, Category.Electronics, "foto-video", "Фото и видео"),
            New(17, Category.Electronics, "audio", "Аудиотехника"),
            // home
            New(18, Category.Home, "mebel", "Мебель"),
            New(19, Category.Home, "bytovaya-tehnika", "Бытовая техника"),
            New(20, Category.Home, "remont-stroyka", "Ремонт и стройка"),
            New(21, Category.Home, "sad-ogorod", "Сад и огород"),
            New(22, Category.Home, "posuda", "Посуда и товары для дома"),
            // fashion
            New(23, Category.Fashion, "muzhskaya-odezhda", "Мужская одежда"),
            New(24, Category.Fashion, "zhenskaya-odezhda", "Женская одежда"),
            New(25, Category.Fashion, "obuv", "Обувь"),
            New(26, Category.Fashion, "aksessuary", "Аксессуары"),
            // kids
            New(27, Category.Kids, "detskaya-odezhda", "Детская одежда и обувь"),
            New(28, Category.Kids, "igrushki", "Игрушки"),
            New(29, Category.Kids, "kolyaski", "Коляски"),
            New(30, Category.Kids, "detskaya-mebel", "Детская мебель"),
            // work
            New(31, Category.Work, "vakansii", "Вакансии"),
            New(32, Category.Work, "rezume", "Резюме"),
            // services
            New(33, Category.Services, "stroitelstvo-remont", "Строительство и ремонт"),
            New(34, Category.Services, "krasota-zdorovie", "Красота и здоровье"),
            New(35, Category.Services, "obuchenie", "Обучение и курсы"),
            New(36, Category.Services, "perevozki", "Перевозки и грузчики"),
            New(37, Category.Services, "remont-tehniki", "Ремонт техники"),
            // animals
            New(38, Category.Animals, "sobaki", "Собаки"),
            New(39, Category.Animals, "koshki", "Кошки"),
            New(40, Category.Animals, "ptitsy", "Птицы"),
            New(41, Category.Animals, "tovary-dlya-zhivotnyh", "Товары для животных"),
            // other
            New(42, Category.Other, "raznoe", "Разное"));
    }

    private static Subcategory New(int id, Category category, string slug, string name) =>
        new() { Id = id, Category = category, Slug = slug, Name = name };
}
