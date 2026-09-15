using GenesisMarket.Api.Telegram.Channel;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>Подпись поста канала: цена, обрезка описания, эскейп, контакты. Без БД и сети.</summary>
public class ChannelPostFormatterTests
{
    private const string Nbsp = " ";
    private const int DefaultDescriptionLimit = 120;

    private static string Caption(
        string title = "Диван угловой",
        string? description = "Почти новый",
        decimal? price = 1200,
        PriceType priceType = PriceType.Fixed,
        City city = City.Tiraspol,
        int descriptionLimit = DefaultDescriptionLimit) =>
        ChannelPostFormatter.BuildCaption(
            new ChannelPostContent(title, description, price, priceType, city), descriptionLimit);

    [Fact]
    public void Caption_follows_post_layout()
    {
        Assert.Equal(
            $"📦 #объявления #Тирасполь\n\nДиван угловой\n\n💰 1{Nbsp}200{Nbsp}руб.\n📍 Тирасполь\n\nПочти новый",
            Caption());
    }

    // ---- Цена: три формы ----

    [Theory]
    [InlineData(1200, "1 200 руб.")]
    [InlineData(15000, "15 000 руб.")]
    [InlineData(1500000, "1 500 000 руб.")]
    [InlineData(500, "500 руб.")]
    public void Fixed_price_is_grouped_with_non_breaking_spaces(int price, string expected)
    {
        Assert.Equal(expected, ChannelPostFormatter.FormatPrice(PriceType.Fixed, price));
        Assert.Contains($"💰 {expected}\n", Caption(price: price));
    }

    [Fact]
    public void Negotiable_price_has_no_currency()
    {
        var caption = Caption(price: null, priceType: PriceType.Negotiable);

        Assert.Contains("💰 Договорная\n", caption);
        Assert.DoesNotContain("руб.", caption);
    }

    [Fact]
    public void Free_price_has_no_currency()
    {
        var caption = Caption(price: 0, priceType: PriceType.Free);

        Assert.Contains("💰 Бесплатно\n", caption);
        Assert.DoesNotContain("руб.", caption);
    }

    // ---- Описание: обрезка ----

    [Fact]
    public void Long_description_is_cut_to_limit_with_ellipsis()
    {
        var caption = Caption(description: new string('а', 500));

        Assert.EndsWith("\n\n" + new string('а', 120) + "…", caption);
    }

    [Fact]
    public void Short_description_is_not_ellipsized()
    {
        Assert.EndsWith("\n\nПочти новый", Caption());
    }

    [Fact]
    public void Cut_does_not_split_emoji_or_leave_trailing_space()
    {
        // 119 символов, пробел, затем эмодзи (суррогатная пара) на границе 120/121.
        var description = new string('б', 118) + " 😀 хвост";

        var caption = Caption(description: description);

        Assert.EndsWith(new string('б', 118) + "…", caption);
    }

    [Fact]
    public void Caption_stays_within_telegram_limit_even_with_generous_description_limit()
    {
        var caption = Caption(title: new string('Т', 120), description: new string('о', 5000), descriptionLimit: 5000);

        // Спецсимволов нет — длина экранированной подписи равна видимой.
        Assert.True(caption.Length <= ChannelPostFormatter.CaptionBudget, $"длина {caption.Length}");
        Assert.True(caption.Length < ChannelPostFormatter.MaxCaptionLength);
        Assert.EndsWith("…", caption);
    }

    [Fact]
    public void Empty_description_omits_block()
    {
        Assert.EndsWith("📍 Тирасполь", Caption(description: "   "));
    }

    // ---- Эскейп ----

    [Fact]
    public void Special_characters_in_title_are_escaped()
    {
        var caption = Caption(title: "Диван <b>\"Лучший\"</b> & кресло");

        Assert.Contains("\n\nДиван &lt;b&gt;\"Лучший\"&lt;/b&gt; &amp; кресло\n\n", caption);
        Assert.DoesNotContain("<b>", caption);
    }

    [Fact]
    public void Description_is_escaped_after_cut_so_entities_are_never_split()
    {
        var caption = Caption(description: new string('&', 300));

        Assert.EndsWith(string.Concat(Enumerable.Repeat("&amp;", 120)) + "…", caption);
    }

    // ---- Город и контакты ----

    [Theory]
    [InlineData("Тирасполь", "Тирасполь")]
    [InlineData("Новые Анены", "НовыеАнены")]
    [InlineData("Сан-Франциско", "СанФранциско")]
    public void City_hashtag_has_no_spaces_or_punctuation(string city, string expected)
    {
        Assert.Equal(expected, ChannelPostFormatter.Hashtag(city));
    }

    [Fact]
    public void Contacts_written_into_text_do_not_reach_post()
    {
        var caption = Caption(
            title: "Диван, звоните 077712345",
            description: "Цена 15 000, торг. Тел +373 777 12 345, пишите @ivan_sell или t.me/ivan_sell, " +
                         "почта ivan@mail.ru, сайт https://shop.example.ru/divan, www.divan.md",
            descriptionLimit: 1000);

        foreach (var leak in new[] { "077712345", "+373", "12 345", "@ivan", "t.me", "ivan@mail.ru", "shop.example", "www.divan" })
            Assert.DoesNotContain(leak, caption);
        Assert.Contains("15 000", caption); // цена в тексте — не телефон
    }

    [Fact]
    public void Sold_caption_keeps_escaped_title_and_adds_label()
    {
        Assert.Equal("Диван &amp; кресло\n\n✅ ПРОДАНО", ChannelPostFormatter.BuildSoldCaption("Диван & кресло"));
    }
}

/// <summary>Рабочее окно и конфигурация публикации в канал.</summary>
public class ChannelScheduleTests
{
    private static readonly TimeZoneInfo UtcPlus2 =
        TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(20, 59, true)]
    [InlineData(21, 0, false)]
    public void Window_is_checked_in_local_time(int localHour, int localMinute, bool expected)
    {
        var utc = new DateTimeOffset(2026, 9, 15, localHour, localMinute, 0, TimeSpan.Zero).AddHours(-2);

        Assert.Equal(expected, ChannelSchedule.IsWithinWindow(utc, UtcPlus2, new TimeOnly(9, 0), new TimeOnly(21, 0)));
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(3, true)]
    [InlineData(12, false)]
    public void Window_across_midnight_is_supported(int utcHour, bool expected)
    {
        var utc = new DateTimeOffset(2026, 9, 15, utcHour, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, ChannelSchedule.IsWithinWindow(utc, TimeZoneInfo.Utc, new TimeOnly(22, 0), new TimeOnly(6, 0)));
    }

    [Fact]
    public void Chisinau_resolves_on_this_platform()
    {
        var zone = ChannelSchedule.ResolveTimeZone("Europe/Chisinau");

        Assert.NotNull(zone);
        Assert.Equal(TimeSpan.FromHours(2), zone.BaseUtcOffset);
    }

    [Fact]
    public void Unknown_time_zone_resolves_to_null() =>
        Assert.Null(ChannelSchedule.ResolveTimeZone("Mars/Olympus_Mons"));

    [Fact]
    public void Options_bind_window_and_intervals_from_configuration()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChannelPublishing:WindowStart"] = "09:30",
                ["ChannelPublishing:WindowEnd"] = "20:15",
                ["ChannelPublishing:MinIntervalMinutes"] = "45",
                ["ChannelPublishing:TimeZoneId"] = "UTC"
            })
            .Build()
            .GetSection(ChannelPublishingOptions.Section)
            .Get<ChannelPublishingOptions>()!;

        Assert.Equal(new TimeOnly(9, 30), options.WindowStart);
        Assert.Equal(new TimeOnly(20, 15), options.WindowEnd);
        Assert.Equal(45, options.MinIntervalMinutes);
        Assert.Equal("UTC", options.TimeZoneId);
    }
}
