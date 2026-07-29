using System.Text.Json;
using SteamLoader.App.Infrastructure.Assets;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// A deliberately small, bundled compatibility catalog. Rules can select
/// trusted TFS behavior, but can never provide commands or executable paths.
/// That keeps future game-specific fixes data-driven without turning a remote
/// file into a code-execution surface.
/// </summary>
internal static class EpicCompatibilityCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, EpicCompatibilityRule>>
        Rules = new(LoadRules);

    public static EpicCompatibilityRule Get(string? appName)
    {
        return !string.IsNullOrWhiteSpace(appName) &&
               Rules.Value.TryGetValue(appName.Trim(), out var rule)
            ? rule
            : EpicCompatibilityRule.Empty;
    }

    private static IReadOnlyDictionary<string, EpicCompatibilityRule> LoadRules()
    {
        try
        {
            var json = EmbeddedAssetReader.ReadText(
                "Assets/omnilibrary-epic-compatibility.json");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rules", out var rules) ||
                rules.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, EpicCompatibilityRule>(
                    StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, EpicCompatibilityRule>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in rules.EnumerateArray())
            {
                var appName = GetString(item, "appName");
                if (!IsSafeAppName(appName))
                {
                    continue;
                }

                var fakeEpicLauncher = item.TryGetProperty(
                        "fakeEpicLauncher",
                        out var fakeEpicLauncherNode) &&
                    fakeEpicLauncherNode.ValueKind == JsonValueKind.True;
                result[appName] = new EpicCompatibilityRule(
                    fakeEpicLauncher);
            }

            return result;
        }
        catch
        {
            // Compatibility data is optional. An unreadable catalog must never
            // prevent normal Epic titles from launching.
            return new Dictionary<string, EpicCompatibilityRule>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool IsSafeAppName(string appName)
    {
        return appName.Length is > 0 and <= 128 &&
               appName.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '-' or '_' or '.');
    }
}

internal sealed record EpicCompatibilityRule(bool FakeEpicLauncher)
{
    public static EpicCompatibilityRule Empty { get; } = new(false);
}
