using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.Metadata;

/// <summary>
/// Translates game descriptions into the user's language without an account
/// or key: Google Translate's public web endpoint first, MyMemory's free API
/// as fallback. Text already in the target language comes back unchanged, and
/// any failure returns null so the caller keeps the original text.
/// </summary>
public sealed class MetadataTranslator
{
    private const int GoogleChunkLength = 1500;
    private const int MyMemoryChunkLength = 450;
    private const int MaxChunks = 8;

    private readonly HttpClient _httpClient;

    public MetadataTranslator(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>Two-letter target language of the Windows UI ("zh-CN"/"zh-TW" for Chinese).</summary>
    public static string CurrentTargetLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        if (language == "zh")
        {
            return culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                   culture.Name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
                   culture.Name.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                ? "zh-TW"
                : "zh-CN";
        }

        return language.Length == 2 ? language : "en";
    }

    public async Task<string?> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return null;
        }

        try
        {
            return await TranslateWithGoogleAsync(text, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        try
        {
            return await TranslateWithMyMemoryAsync(text, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string> TranslateWithGoogleAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(text.Length);
        foreach (var chunk in ChunksOrThrow(text, GoogleChunkLength))
        {
            var url =
                "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t" +
                $"&tl={Uri.EscapeDataString(targetLanguage)}&q={Uri.EscapeDataString(chunk)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var translated = ParseGoogleResponse(document.RootElement);
            if (translated is null)
            {
                throw new InvalidOperationException("Unexpected translation response.");
            }

            AppendChunk(output, translated);
        }

        return output.ToString();
    }

    private async Task<string> TranslateWithMyMemoryAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(text.Length);
        foreach (var chunk in ChunksOrThrow(text, MyMemoryChunkLength))
        {
            // MyMemory needs a source language; game sites mostly write English.
            var url =
                $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}" +
                $"&langpair=en|{Uri.EscapeDataString(targetLanguage)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var status = root.TryGetProperty("responseStatus", out var statusNode) &&
                         statusNode.ValueKind == JsonValueKind.Number
                ? statusNode.GetInt32()
                : 200;
            if (status != 200 ||
                !root.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translatedNode) ||
                translatedNode.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Unexpected translation response.");
            }

            AppendChunk(output, translatedNode.GetString() ?? string.Empty);
        }

        return output.ToString();
    }

    /// <summary>[[["Hallo","Hello",...],...],null,"en",...] -> "Hallo".</summary>
    internal static string? ParseGoogleResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array ||
            root.GetArrayLength() == 0 ||
            root[0].ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array &&
                segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(segment[0].GetString());
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    // A half translated description is worse than the original one.
    private static IReadOnlyList<string> ChunksOrThrow(string text, int maxLength)
    {
        var chunks = SplitIntoChunks(text, maxLength);
        return chunks.Count <= MaxChunks
            ? chunks
            : throw new InvalidOperationException("The text is too long to translate.");
    }

    /// <summary>Splits at sentence ends (or spaces) so no chunk exceeds the limit.</summary>
    internal static IReadOnlyList<string> SplitIntoChunks(string text, int maxLength)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining);
                break;
            }

            var window = remaining[..maxLength];
            var cut = Math.Max(
                Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf("! ", StringComparison.Ordinal)),
                window.LastIndexOf("? ", StringComparison.Ordinal));
            cut = cut > maxLength / 3 ? cut + 1 : window.LastIndexOf(' ');
            if (cut <= 0)
            {
                cut = maxLength;
            }

            chunks.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].Trim();
        }

        return chunks;
    }

    private static void AppendChunk(StringBuilder output, string chunk)
    {
        if (output.Length > 0)
        {
            output.Append(' ');
        }
        output.Append(chunk.Trim());
    }
}
