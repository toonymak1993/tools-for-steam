using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.AppStart;

public sealed class AppStartService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] IgnoredNameParts =
    [
        "uninstall",
        "readme",
        "manual",
        "documentation",
        "help",
        "website",
        "support",
        "license",
        "eula",
        "update",
        "updater"
    ];

    private readonly string _settingsPath;
    private readonly object _gate = new();

    public AppStartService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppStartSnapshot GetSnapshot()
    {
        var settings = LoadSettings();
        var shortcuts = settings.Shortcuts
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut.Id))
            .Select(ToState)
            .OrderBy(shortcut => shortcut.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AppStartSnapshot(
            shortcuts,
            shortcuts.Length switch
            {
                0 => "No app shortcuts added yet.",
                1 => "1 app shortcut ready.",
                _ => $"{shortcuts.Length} app shortcuts ready.",
            });
    }

    public AppStartCatalogSnapshot GetCatalog()
    {
        var savedIds = LoadSettings()
            .Shortcuts
            .Select(shortcut => shortcut.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var apps = EnumerateStartMenuShortcuts()
            .Select(entry => entry with { Added = savedIds.Contains(entry.Id) })
            .OrderBy(entry => entry.Added)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AppStartCatalogSnapshot(
            apps,
            apps.Length switch
            {
                0 => "No Start Menu apps were detected.",
                1 => "1 Start Menu app detected.",
                _ => $"{apps.Length} Start Menu apps detected.",
            });
    }

    public AppStartSnapshot AddShortcut(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("An app ID is required.");
        }

        var app = EnumerateStartMenuShortcuts().FirstOrDefault(entry =>
            string.Equals(entry.Id, appId, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            throw new InvalidOperationException("The selected app is no longer available.");
        }

        lock (_gate)
        {
            var settings = LoadSettingsNoLock();
            var existingIndex = settings.Shortcuts.FindIndex(shortcut =>
                string.Equals(shortcut.Id, app.Id, StringComparison.OrdinalIgnoreCase));

            var shortcut = new AppStartShortcutConfiguration
            {
                Id = app.Id,
                Name = app.Name,
                SourcePath = app.SourcePath,
                IconDataUri = app.IconDataUri
            };

            if (existingIndex >= 0)
            {
                settings.Shortcuts[existingIndex] = shortcut;
            }
            else
            {
                settings.Shortcuts.Add(shortcut);
            }

            SaveSettingsNoLock(settings);
        }

        return GetSnapshot();
    }

    public AppStartSnapshot RemoveShortcut(string shortcutId)
    {
        if (string.IsNullOrWhiteSpace(shortcutId))
        {
            throw new InvalidOperationException("An app shortcut ID is required.");
        }

        lock (_gate)
        {
            var settings = LoadSettingsNoLock();
            settings.Shortcuts.RemoveAll(shortcut =>
                string.Equals(shortcut.Id, shortcutId, StringComparison.OrdinalIgnoreCase));
            SaveSettingsNoLock(settings);
        }

        return GetSnapshot();
    }

    public AppStartSnapshot LaunchShortcut(string shortcutId)
    {
        if (string.IsNullOrWhiteSpace(shortcutId))
        {
            throw new InvalidOperationException("An app shortcut ID is required.");
        }

        var shortcut = LoadSettings()
            .Shortcuts
            .FirstOrDefault(entry => string.Equals(entry.Id, shortcutId, StringComparison.OrdinalIgnoreCase));
        if (shortcut is null)
        {
            throw new InvalidOperationException("The selected app shortcut was not found.");
        }

        if (!File.Exists(shortcut.SourcePath))
        {
            throw new InvalidOperationException("The selected app shortcut no longer exists.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = shortcut.SourcePath,
            UseShellExecute = true
        })?.Dispose();

        return GetSnapshot();
    }

    private AppStartSettingsData LoadSettings()
    {
        lock (_gate)
        {
            return LoadSettingsNoLock();
        }
    }

    private AppStartSettingsData LoadSettingsNoLock()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppStartSettingsData();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppStartSettingsData>(json, JsonOptions)
                ?? new AppStartSettingsData();
        }
        catch
        {
            return new AppStartSettingsData();
        }
    }

    private void SaveSettingsNoLock(AppStartSettingsData settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static AppStartShortcutState ToState(AppStartShortcutConfiguration shortcut)
    {
        return new AppStartShortcutState(
            shortcut.Id,
            shortcut.Name,
            shortcut.SourcePath,
            string.IsNullOrWhiteSpace(shortcut.IconDataUri)
                ? CreateIconDataUri(shortcut.Name)
                : shortcut.IconDataUri);
    }

    private static IReadOnlyList<AppStartCatalogEntry> EnumerateStartMenuShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        var byId = new Dictionary<string, AppStartCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var shortcutPath in EnumerateShortcutFiles(root))
            {
                var name = CleanShortcutName(Path.GetFileNameWithoutExtension(shortcutPath));
                if (ShouldIgnoreShortcut(name))
                {
                    continue;
                }

                var id = CreateStableId(shortcutPath);
                byId[id] = new AppStartCatalogEntry(
                    id,
                    name,
                    shortcutPath,
                    TryCreateShellIconDataUri(shortcutPath) ?? CreateIconDataUri(name),
                    Added: false);
            }
        }

        return byId.Values.ToArray();
    }

    private static IEnumerable<string> EnumerateShortcutFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                .Where(File.Exists)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool ShouldIgnoreShortcut(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return IgnoredNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanShortcutName(string name)
    {
        var cleaned = name.Trim();
        foreach (var suffix in new[] { " shortcut", " launcher" })
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].Trim();
            }
        }

        return cleaned;
    }

    private static string CreateStableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string CreateIconDataUri(string name)
    {
        var initials = BuildInitials(name);
        var hue = CreateHue(name);
        var background = $"hsl({hue} 28% 24%)";
        var accent = $"hsl({hue} 58% 66%)";
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96">
              <rect width="96" height="96" rx="22" fill="{background}"/>
              <rect x="18" y="18" width="60" height="60" rx="18" fill="rgba(255,255,255,.08)"/>
              <text x="48" y="58" text-anchor="middle" font-family="Arial, sans-serif" font-size="28" font-weight="700" fill="{accent}">{HtmlEncoder.Default.Encode(initials)}</text>
            </svg>
            """;

        return "data:image/svg+xml;charset=utf-8," + Uri.EscapeDataString(svg);
    }

    private static string? TryCreateShellIconDataUri(string path)
    {
        try
        {
            var result = SHGetFileInfo(
                path,
                0,
                out var fileInfo,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon);

            if (result == 0 || fileInfo.IconHandle == 0)
            {
                return null;
            }

            try
            {
                using var icon = (Icon)Icon.FromHandle(fileInfo.IconHandle).Clone();
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
            finally
            {
                DestroyIcon(fileInfo.IconHandle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string BuildInitials(string name)
    {
        var parts = name
            .Split([' ', '-', '_', '.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => char.IsLetterOrDigit(part[0]))
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0]).ToString())
            .ToArray();

        return parts.Length > 0 ? string.Concat(parts) : "A";
    }

    private static int CreateHue(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (hash[0] * 256 + hash[1]) % 360;
    }

    private sealed record AppStartSettingsData
    {
        public List<AppStartShortcutConfiguration> Shortcuts { get; init; } = [];
    }

    private sealed record AppStartShortcutConfiguration
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string SourcePath { get; init; } = string.Empty;

        public string? IconDataUri { get; init; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint IconHandle;

        public int IconIndex;

        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
