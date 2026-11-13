using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Metadata;

/// <summary>Game info read from a public IGN game page.</summary>
public sealed record IgnGameInfo(
    string Slug,
    string Name,
    string ShortDescription,
    string Description,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    string ReleaseDate,
    int? Rating,
    string ImageUrl,
    string PageUrl);

/// <summary>
/// Reads game info from IGN's public game pages (www.ign.com/games/&lt;slug&gt;).
/// IGN has no public API, so the page's embedded page data is parsed and every
/// field is optional. Only a page whose game name matches the requested title
/// exactly is used: a wrong game is worse than no game info.
/// </summary>
public sealed partial class IgnMetadataClient
{
    public const string PageBaseUrl = "https://www.ign.com/games/";

    private const int MaxListItems = 8;

    private readonly HttpClient _httpClient;

    public IgnMetadataClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Tries the IGN page slugs for each title variant and returns the first
    /// page whose game name matches one of the variants.
    /// </summary>
    public async Task<IgnGameInfo?> FindAsync(
        IEnumerable<string> titleVariants,
        CancellationToken cancellationToken)
    {
        var variants = titleVariants
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var wanted = variants
            .Select(NormalizeName)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var triedSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            foreach (var slug in CandidateSlugs(variant))
            {
                if (!triedSlugs.Add(slug) || triedSlugs.Count > 4)
                {
                    continue;
                }

                var info = await FetchAsync(slug, cancellationToken).ConfigureAwait(false);
                if (info is not null && wanted.Contains(NormalizeName(info.Name)))
                {
                    return info;
                }
            }
        }

        return null;
    }

    /// <summary>Loads one IGN game page; null when it does not exist.</summary>
    public async Task<IgnGameInfo?> FetchAsync(string slug, CancellationToken cancellationToken)
    {
        if (!IsValidSlug(slug))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, PageBaseUrl + slug);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ToolsForSteam");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return Parse(html, slug);
    }

    /// <summary>"Assassin's Creed: Odyssey™" -> "assassins-creed-odyssey".</summary>
    public static string Slugify(string title)
    {
        var decoded = WebUtility.HtmlDecode(title ?? string.Empty)
            .Replace("™", string.Empty, StringComparison.Ordinal)
            .Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("©", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decoded.Length);
        foreach (var character in decoded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return NonSlugPattern().Replace(builder.ToString(), "-").Trim('-');
    }

    public static IEnumerable<string> CandidateSlugs(string title)
    {
        var slug = Slugify(title);
        if (slug.Length == 0)
        {
            yield break;
        }

        yield return slug;
        if (slug.StartsWith("the-", StringComparison.Ordinal) && slug.Length > 4)
        {
            yield return slug[4..];
        }
    }

    /// <summary>Accepts an IGN page URL or a bare slug.</summary>
    public static string? ParseSlug(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.EndsWith("ign.com", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            text = segments.Length >= 2 && segments[0].Equals("games", StringComparison.OrdinalIgnoreCase)
                ? segments[1]
                : string.Empty;
        }

        text = text.ToLowerInvariant();
        return IsValidSlug(text) ? text : null;
    }

    private static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= 120 && ValidSlugPattern().IsMatch(slug);

    internal static IgnGameInfo? Parse(string html, string slug)
    {
        var match = NextDataPattern().Match(html ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(match.Groups["json"].Value);
            if (!TryGet(document.RootElement, out var page, "props", "pageProps", "page") ||
                page.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = Text(page, "type");
            if (type.Length > 0 && !type.Equals("Game", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var name = TryGet(page, out var names, "metadata", "names")
                ? Text(names, "name")
                : string.Empty;
            if (name.Length == 0)
            {
                name = Text(page, "name");
            }
            if (name.Length == 0)
            {
                return null;
            }

            var shortDescription = string.Empty;
            var description = string.Empty;
            if (TryGet(page, out var descriptions, "metadata", "descriptions"))
            {
                shortDescription = Clean(Text(descriptions, "short"));
                description = Clean(Text(descriptions, "long"));
            }
            if (description.Length == 0)
            {
                description = shortDescription;
            }

            int? rating = null;
            if (page.TryGetProperty("primaryReview", out var review) &&
                review.ValueKind == JsonValueKind.Object &&
                review.TryGetProperty("score", out var scoreNode) &&
                scoreNode.ValueKind == JsonValueKind.Number &&
                scoreNode.TryGetDouble(out var score) &&
                score is >= 0 and <= 10)
            {
                rating = (int)Math.Round(score * 10, MidpointRounding.AwayFromZero);
            }

            var imageUrl = TryGet(page, out var image, "primaryImage") ? Text(image, "url") : string.Empty;
            if (!imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                imageUrl = string.Empty;
            }

            return new IgnGameInfo(
                slug,
                WebUtility.HtmlDecode(name).Trim(),
                shortDescription,
                description,
                Names(page, "producers"),
                Names(page, "publishers"),
                Strings(page, "genreNames"),
                Text(page, "releaseDate"),
                rating,
                imageUrl,
                PageBaseUrl + slug);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lowercase letters and digits only, for exact name matching.</summary>
    public static string NormalizeName(string value) =>
        NonAlphaNumericPattern().Replace(Slugify(value), " ").Trim();

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string Clean(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(HtmlTagPattern().Replace(value, " ")), @"\s+", " ").Trim();

    // IGN sends a single attribute object or a list, depending on the count.
    private static IReadOnlyList<string> Names(JsonElement page, string property)
    {
        if (!page.TryGetProperty(property, out var value))
        {
            return [];
        }

        var items = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(item => Text(item, "name")),
            JsonValueKind.Object => [Text(value, "name")],
            _ => [],
        };
        return Limit(items);
    }

    private static IReadOnlyList<string> Strings(JsonElement page, string property)
    {
        if (!page.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return Limit(value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty));
    }

    private static IReadOnlyList<string> Limit(IEnumerable<string> values) =>
        values
            .Select(value => WebUtility.HtmlDecode(value).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListItems)
            .ToArray();

    [GeneratedRegex(@"<script id=""__NEXT_DATA__"" type=""application/json""[^>]*>(?<json>[\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex NextDataPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumericPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ValidSlugPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();
}
