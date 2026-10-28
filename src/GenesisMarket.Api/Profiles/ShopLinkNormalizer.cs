using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Profiles;

public static partial class ShopLinkNormalizer
{
    [GeneratedRegex("^[A-Za-z0-9._]{1,30}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex InstagramPattern();

    public static bool TryInstagram(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var value = input.Trim();
        if (value.StartsWith("instagram.com/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("www.instagram.com/", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https") ||
                !uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase))
                return false;

            value = uri.AbsolutePath.Trim('/');
            if (value.Contains('/'))
                return false;
        }
        else
        {
            value = value.TrimStart('@');
            if (value.StartsWith("instagram.com/", StringComparison.OrdinalIgnoreCase))
                value = value["instagram.com/".Length..].TrimEnd('/');
            else if (value.StartsWith("www.instagram.com/", StringComparison.OrdinalIgnoreCase))
                value = value["www.instagram.com/".Length..].TrimEnd('/');
        }

        if (!InstagramPattern().IsMatch(value))
            return false;

        normalized = value;
        return true;
    }

    public static bool TryWebsite(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var value = input.Trim();
        if (!Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            value = "https://" + value;

        if (value.Length > 200 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
            return false;

        normalized = uri.ToString();
        return normalized.Length <= 200;
    }
}

public static partial class TelegramUsernameNormalizer
{
    [GeneratedRegex("^[A-Za-z0-9_]{5,32}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex UsernamePattern();

    public static bool TryNormalize(string? input, out string? normalized)
    {
        normalized = null;
        if (input is null)
            return false;

        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https") ||
                !uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0)
                return false;

            value = uri.AbsolutePath.Trim('/');
            if (value.Contains('/'))
                return false;
        }
        else if (value.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["t.me/".Length..].TrimEnd('/');
        }

        value = value.TrimStart('@');
        if (!UsernamePattern().IsMatch(value))
            return false;

        normalized = value;
        return true;
    }
}
