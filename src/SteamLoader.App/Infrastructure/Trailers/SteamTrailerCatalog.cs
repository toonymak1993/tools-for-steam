using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.Trailers;

/// <summary>
/// Pure helpers for Steam's public store endpoints: trailer lists from
/// <c>api/appdetails?filters=movies</c> and title matches from
/// <c>api/storesearch</c>. Kept free of I/O so they can be unit tested.
/// </summary>
public static partial class SteamTrailerCatalog
{
    private static readonly string[] EditionSuffixes =
    [
        "game of the year edition",
        "goty edition",
        "definitive edition",
        "complete edition",
        "deluxe edition",
        "ultimate edition",
        "standard edition",
        "enhanced edition",
        "remastered",
        "goty",
    ];

    public static string BuildAppDetailsUrl(uint appId) =>
        $"https://store.steampowered.com/api/appdetails?appids={appId.ToString(CultureInfo.InvariantCulture)}&filters=movies&l=english";

    public static string BuildStoreSearchUrl(string title) =>
        $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title.Trim())}&l=english&cc=US";

    /// <summary>Reads the movie list of one app; an unknown app or no videos gives an empty list.</summary>
    public static IReadOnlyList<TrailerMovie> ParseMovies(string json, uint appId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out var wrapper) ||
                !wrapper.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !wrapper.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("movies", out var movies) ||
                movies.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<TrailerMovie>();
            foreach (var movie in movies.EnumerateArray())
            {
                if (movie.ValueKind != JsonValueKind.Object ||
                    !movie.TryGetProperty("id", out var idNode) ||
                    !idNode.TryGetInt64(out var id))
                {
                    continue;
                }

                var dash = ReadHttpsUrl(movie, "dash_h264");
                var hls = ReadHttpsUrl(movie, "hls_h264");
                if (dash.Length == 0 && hls.Length == 0)
                {
                    continue;
                }

                result.Add(new TrailerMovie(
                    id,
                    ReadString(movie, "name"),
                    ReadHttpsUrl(movie, "thumbnail"),
                    dash,
                    hls,
                    movie.TryGetProperty("highlight", out var highlight) && highlight.ValueKind == JsonValueKind.True));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The video to play: the chosen one if it still exists, else the first highlighted, else the first.</summary>
    public static TrailerMovie? PickMovie(IReadOnlyList<TrailerMovie> movies, long? preferredId)
    {
        if (movies.Count == 0)
        {
            return null;
        }

        if (preferredId is > 0)
        {
            var preferred = movies.FirstOrDefault(movie => movie.Id == preferredId.Value);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return movies.FirstOrDefault(movie => movie.Highlight) ?? movies[0];
    }

    public static IReadOnlyList<TrailerStoreSearchResult> ParseStoreSearch(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<TrailerStoreSearchResult>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = ReadString(item, "type");
                if (type.Length > 0 && !type.Equals("app", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idNode) || !idNode.TryGetUInt32(out var appId) || appId == 0)
                {
                    continue;
                }

                var name = ReadString(item, "name");
                if (name.Length > 0)
                {
                    result.Add(new TrailerStoreSearchResult(appId, name));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Picks the store result that is the same game as <paramref name="title"/>.
    /// Only a clear match counts: equal normalized names, or equal once edition
    /// words are removed. A wrong trailer is worse than none.
    /// </summary>
    public static TrailerStoreSearchResult? FindBestMatch(string title, IReadOnlyList<TrailerStoreSearchResult> results)
    {
        var wanted = NormalizeTitle(title);
        if (wanted.Length == 0 || results.Count == 0)
        {
            return null;
        }

        var exact = results.FirstOrDefault(result => NormalizeTitle(result.Name) == wanted);
        if (exact is not null)
        {
            return exact;
        }

        var wantedCore = StripEdition(wanted);
        return results.FirstOrDefault(result => StripEdition(NormalizeTitle(result.Name)) == wantedCore && wantedCore.Length > 0);
    }

    /// <summary>Lower case, no trademark signs or punctuation, single spaces, roman numerals kept.</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var decomposed = title.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is '™' or '®' or '©')
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        var text = builder.ToString().Replace(" and ", " ", StringComparison.Ordinal);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    internal static string StripEdition(string normalizedTitle)
    {
        var text = normalizedTitle;
        foreach (var suffix in EditionSuffixes)
        {
            if (text.EndsWith(" " + suffix, StringComparison.Ordinal))
            {
                text = text[..^(suffix.Length + 1)].TrimEnd();
            }
        }

        return text;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string ReadHttpsUrl(JsonElement element, string property)
    {
        var value = ReadString(element, property);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : string.Empty;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
