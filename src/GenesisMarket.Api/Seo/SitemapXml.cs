using System.Globalization;
using System.Text;
using System.Xml;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Разметка карты сайта по протоколу Sitemap 0.9. Пишем через <see cref="XmlWriter"/>, а не
/// конкатенацией строк: спецсимволы (&amp;, &lt;, &gt;, кавычки) в адресах экранируются
/// автоматически — это важно для URL с query-параметрами (<c>/catalog?category=...</c>) и
/// для slug'ов, если в них когда-нибудь попадёт что-то кроме латиницы и дефисов.
/// </summary>
public static class SitemapXml
{
    public const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    /// <summary>Формат даты для &lt;lastmod&gt;: ISO 8601 в UTC (допустимый по протоколу W3C Datetime).</summary>
    private const string LastModFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = false,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };

    /// <summary>&lt;urlset&gt; со списком страниц — готовый к отдаче документ в UTF-8.</summary>
    public static byte[] RenderUrlSet(IReadOnlyList<SitemapUrl> urls)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", Ns);

            foreach (var url in urls)
            {
                writer.WriteStartElement("url", Ns);
                writer.WriteElementString("loc", Ns, url.Loc);
                if (url.LastMod is { } lastMod)
                    writer.WriteElementString("lastmod", Ns,
                        lastMod.ToUniversalTime().ToString(LastModFormat, CultureInfo.InvariantCulture));
                writer.WriteElementString("changefreq", Ns, url.ChangeFreq);
                writer.WriteElementString("priority", Ns,
                    url.Priority.ToString("0.0", CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return buffer.ToArray();
    }

    /// <summary>&lt;sitemapindex&gt; со ссылками на файлы карты — для режима разбивки.</summary>
    public static byte[] RenderIndex(IReadOnlyList<string> sitemapLocations)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, Settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("sitemapindex", Ns);

            foreach (var loc in sitemapLocations)
            {
                writer.WriteStartElement("sitemap", Ns);
                writer.WriteElementString("loc", Ns, loc);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return buffer.ToArray();
    }
}
