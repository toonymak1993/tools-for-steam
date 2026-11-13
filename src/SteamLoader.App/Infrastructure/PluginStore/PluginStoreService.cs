using System.Net;
using System.Text;
using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.PluginStore;

/// <summary>
/// Plugin Activator: turns the integrations that ship with Tools for Steam on
/// or off. Tools for Steam is one fixed tool; there is no community plugin
/// catalog, package installer or third-party plugin runtime.
/// </summary>
public sealed class PluginStoreService
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInImageAccents =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["app-start"] = "#3FA7FF",
            ["artwork"] = "#F97316",
            ["audio"] = "#10B981",
            ["auto-sisr"] = "#F43F5E",
            ["discord"] = "#5865F2",
            ["hltb"] = "#8B5CF6",
            ["metadata"] = "#0EA5E9",
            ["overture"] = "#F59E0B",
            ["performance"] = "#22C55E",
            ["power"] = "#FB7185",
            ["processes"] = "#14B8A6",
            ["smart-home"] = "#06B6D4",
            ["store-sync"] = "#60A5FA",
            ["store"] = "#5EE6A8",
            ["themes"] = "#A855F7",
            ["trailers"] = "#E11D48"
        };

    private readonly SteamLoaderSettingsService _settingsService;
    private readonly string _builtInImagesRootPath;
    private readonly object _gate = new();
    private readonly List<PluginStoreInputState> _inputQueue = [];
    private bool _overlayOpen;
    private long _inputNonce;

    public PluginStoreService(
        HttpClient httpClient,
        SteamLoaderSettingsService settingsService,
        string rootPath)
    {
        _ = httpClient;
        _settingsService = settingsService;
        _builtInImagesRootPath = Path.Combine(rootPath, "images", "built-in");

        EnsureBuiltInImageCache();
    }

    public Task<PluginStoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var settingsSnapshot = _settingsService.GetSnapshot();
        var builtInById = settingsSnapshot.Plugins.ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
        var builtInPlugins = SteamLoaderPluginCatalog.Definitions
            .Select(plugin =>
            {
                builtInById.TryGetValue(plugin.Id, out var pluginSettings);
                var enabled = plugin.CanDisable ? pluginSettings?.Enabled ?? plugin.DefaultEnabled : true;
                var statusText = !plugin.CanDisable
                    ? "Required system plugin."
                    : enabled
                        ? "Active"
                        : "Inactive";

                return new PluginStorePluginState(
                    plugin.Id,
                    plugin.Title,
                    plugin.Description,
                    Source: "Tools for Steam",
                    Author: "Tools for Steam",
                    Category: "Included",
                    Version: settingsSnapshot.ProductVersion,
                    IsBuiltIn: true,
                    IsInstalled: true,
                    IsEnabled: enabled,
                    CanToggleVisibility: plugin.CanDisable,
                    StatusText: statusText,
                    Tags: ["Included"],
                    Images: BuildBuiltInImages(plugin.Id, plugin.Title));
            })
            .ToArray();

        return Task.FromResult(new PluginStoreSnapshot(
            StatusText: $"{builtInPlugins.Count(plugin => plugin.IsEnabled)} of {builtInPlugins.Length} plugins active.",
            ErrorText: string.Empty,
            CatalogTitle: "Plugin Activator",
            CatalogDescription: "Turn the plugins included with Tools for Steam on or off.",
            BuiltInCount: builtInPlugins.Length,
            BuiltInPlugins: builtInPlugins));
    }

    public Task<PluginStoreSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotAsync(cancellationToken);
    }

    public bool TryGetBuiltInImage(string pluginId, out string imagePath, out string contentType)
    {
        imagePath = string.Empty;
        contentType = string.Empty;

        if (!TryResolveBuiltInImagePath(pluginId, out var candidatePath))
        {
            return false;
        }

        imagePath = candidatePath;
        contentType = GetImageContentType(candidatePath);
        return true;
    }

    public PluginStoreOverlayState GetOverlayState()
    {
        lock (_gate)
        {
            return new PluginStoreOverlayState(_overlayOpen);
        }
    }

    public PluginStoreOverlayState SetOverlayOpen(bool open)
    {
        lock (_gate)
        {
            _overlayOpen = open;
            if (!open)
            {
                _inputQueue.Clear();
            }

            return new PluginStoreOverlayState(_overlayOpen);
        }
    }

    public PluginStoreInputState AddOverlayInput(string action, string source)
    {
        var normalizedAction = NormalizeInputAction(action);
        if (normalizedAction.Length == 0)
        {
            throw new InvalidOperationException("A supported store input action is required.");
        }

        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        if (normalizedSource.Length > 80)
        {
            normalizedSource = normalizedSource[..80];
        }

        lock (_gate)
        {
            var input = new PluginStoreInputState(++_inputNonce, normalizedAction, normalizedSource);
            _inputQueue.Add(input);

            if (_inputQueue.Count > 48)
            {
                _inputQueue.RemoveRange(0, _inputQueue.Count - 48);
            }

            return input;
        }
    }

    public PluginStoreInputBatch GetOverlayInputs(long afterNonce)
    {
        lock (_gate)
        {
            var inputs = _inputQueue
                .Where(input => input.Nonce > afterNonce)
                .ToArray();

            return new PluginStoreInputBatch(_inputNonce, inputs);
        }
    }

    private IReadOnlyList<PluginStoreImageState> BuildBuiltInImages(string pluginId, string title)
    {
        return TryResolveBuiltInImagePath(pluginId, out _)
            ? [new PluginStoreImageState($"api/plugin-activator/images/{Uri.EscapeDataString(pluginId)}", $"{title} preview")]
            : [];
    }

    private static string GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "image/png"
        };
    }

    private void EnsureBuiltInImageCache()
    {
        Directory.CreateDirectory(_builtInImagesRootPath);

        foreach (var plugin in SteamLoaderPluginCatalog.Definitions)
        {
            var normalizedPluginId = NormalizePluginId(plugin.Id);
            if (normalizedPluginId.Length == 0)
            {
                continue;
            }

            var imagePath = Path.Combine(_builtInImagesRootPath, $"{normalizedPluginId}.svg");
            if (File.Exists(imagePath))
            {
                continue;
            }

            File.WriteAllText(
                imagePath,
                BuildBuiltInPreviewSvg(normalizedPluginId, plugin.Title, plugin.Description),
                Encoding.UTF8);
        }
    }

    private bool TryResolveBuiltInImagePath(string pluginId, out string imagePath)
    {
        imagePath = string.Empty;

        var normalizedPluginId = NormalizePluginId(pluginId);
        if (normalizedPluginId.Length == 0)
        {
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Combine(_builtInImagesRootPath, $"{normalizedPluginId}.svg"));
        EnsureWithinPathRoot(candidatePath, _builtInImagesRootPath);
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        imagePath = candidatePath;
        return true;
    }

    private static string BuildBuiltInPreviewSvg(string pluginId, string title, string description)
    {
        var accent = BuiltInImageAccents.TryGetValue(pluginId, out var configuredAccent)
            ? configuredAccent
            : "#60A5FA";
        var escapedTitle = EscapeSvgText(title);
        var escapedDescription = EscapeSvgText(TruncateForSvg(description, 88));
        var escapedLabel = EscapeSvgText(pluginId.Replace('-', ' ').ToUpperInvariant());

        return $$"""
                 <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1280 720" role="img" aria-label="{{escapedTitle}}">
                   <defs>
                     <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                       <stop offset="0%" stop-color="#0B1120" />
                       <stop offset="100%" stop-color="#172554" />
                     </linearGradient>
                     <linearGradient id="accent" x1="0" y1="0" x2="1" y2="1">
                       <stop offset="0%" stop-color="{{accent}}" stop-opacity="1" />
                       <stop offset="100%" stop-color="{{accent}}" stop-opacity="0.18" />
                     </linearGradient>
                   </defs>
                   <rect width="1280" height="720" rx="36" fill="url(#bg)" />
                   <circle cx="1100" cy="120" r="210" fill="url(#accent)" />
                   <circle cx="1190" cy="610" r="180" fill="{{accent}}" fill-opacity="0.14" />
                   <rect x="74" y="74" width="1132" height="572" rx="32" fill="#07101D" fill-opacity="0.56" stroke="{{accent}}" stroke-opacity="0.28" />
                   <text x="116" y="176" fill="{{accent}}" font-family="Segoe UI, Arial, sans-serif" font-size="34" font-weight="700" letter-spacing="5">{{escapedLabel}}</text>
                   <text x="116" y="306" fill="#F8FAFC" font-family="Segoe UI, Arial, sans-serif" font-size="86" font-weight="700">{{escapedTitle}}</text>
                   <text x="116" y="386" fill="#CBD5E1" font-family="Segoe UI, Arial, sans-serif" font-size="34">{{escapedDescription}}</text>
                   <text x="116" y="560" fill="#E2E8F0" font-family="Segoe UI, Arial, sans-serif" font-size="28">Built-in plugin preview</text>
                 </svg>
                 """;
    }

    private static string EscapeSvgText(string value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static string TruncateForSvg(string value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxLength - 1)].TrimEnd()}...";
    }

    private static void EnsureWithinPathRoot(string path, string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);

        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested image path is outside the plugin image directory.");
        }
    }

    private static string NormalizePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return string.Empty;
        }

        var filtered = new string(pluginId
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray());

        return filtered.Trim('.', '-', '_');
    }

    private static string NormalizeInputAction(string? action)
    {
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "up" or "down" or "left" or "right" or "a" or "b" or "search-back" or "previous-section" or "next-section"
            ? normalized
            : string.Empty;
    }
}
