using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.AppStart;

public sealed class AppStartService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromMinutes(10);

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
    private readonly string _catalogPath;
    private readonly Func<AppStartDiscoveryResult> _discoverApps;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ProcessWindowService? _processWindowService;
    private readonly object _gate = new();
    private AppStartCatalogData? _catalog;

    public AppStartService(string settingsPath)
        : this(settingsPath, DiscoverInstalledApps, () => DateTimeOffset.UtcNow, null)
    {
    }

    public AppStartService(string settingsPath, ProcessWindowService processWindowService)
        : this(settingsPath, DiscoverInstalledApps, () => DateTimeOffset.UtcNow, processWindowService)
    {
    }

    internal AppStartService(
        string settingsPath,
        Func<AppStartDiscoveryResult> discoverApps,
        Func<DateTimeOffset>? utcNow = null,
        ProcessWindowService? processWindowService = null)
    {
        _settingsPath = settingsPath;
        _catalogPath = Path.Combine(
            Path.GetDirectoryName(settingsPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(settingsPath)}-catalog.json");
        _discoverApps = discoverApps;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _processWindowService = processWindowService;
    }

    public AppStartSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var settings = LoadSettingsNoLock();
            var catalog = EnsureCatalogNoLock();
            var favorites = settings.FavoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hidden = settings.HiddenIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shortcuts = catalog.Entries
                .Where(entry => !hidden.Contains(entry.Id))
                .Select(entry => ToShortcutState(entry, favorites.Contains(entry.Id)))
                .OrderByDescending(entry => entry.Favorite)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var favoriteCount = shortcuts.Count(entry => entry.Favorite);

            return new AppStartSnapshot(
                shortcuts,
                shortcuts.Length switch
                {
                    0 => "No launchable Windows apps were detected.",
                    1 => favoriteCount == 1 ? "1 app ready · 1 favorite." : "1 app ready.",
                    _ when favoriteCount > 0 => $"{shortcuts.Length} apps ready · {favoriteCount} favorites.",
                    _ => $"{shortcuts.Length} apps ready."
                },
                catalog.LastScanUtc);
        }
    }

    public AppStartCatalogSnapshot GetCatalog()
    {
        lock (_gate)
        {
            return BuildCatalogSnapshotNoLock(EnsureCatalogNoLock());
        }
    }

    public AppStartCatalogSnapshot RefreshCatalog()
    {
        lock (_gate)
        {
            return BuildCatalogSnapshotNoLock(RefreshCatalogNoLock());
        }
    }

    public AppStartSnapshot AddShortcut(string appId)
    {
        ValidateAppId(appId);

        lock (_gate)
        {
            EnsureAppExistsNoLock(appId);
            var settings = LoadSettingsNoLock();
            RemoveId(settings.HiddenIds, appId);
            SaveSettingsNoLock(settings);
        }

        return GetSnapshot();
    }

    public AppStartSnapshot RemoveShortcut(string shortcutId)
    {
        ValidateAppId(shortcutId);

        lock (_gate)
        {
            var settings = LoadSettingsNoLock();
            AddId(settings.HiddenIds, shortcutId);
            RemoveId(settings.FavoriteIds, shortcutId);
            SaveSettingsNoLock(settings);
        }

        return GetSnapshot();
    }

    public AppStartSnapshot ToggleFavorite(string shortcutId)
    {
        ValidateAppId(shortcutId);

        lock (_gate)
        {
            EnsureAppExistsNoLock(shortcutId);
            var settings = LoadSettingsNoLock();
            RemoveId(settings.HiddenIds, shortcutId);
            if (!RemoveId(settings.FavoriteIds, shortcutId))
            {
                AddId(settings.FavoriteIds, shortcutId);
            }

            SaveSettingsNoLock(settings);
        }

        return GetSnapshot();
    }

    public AppStartSnapshot LaunchShortcut(string shortcutId)
    {
        ValidateAppId(shortcutId);

        AppStartCatalogConfiguration app;
        lock (_gate)
        {
            app = EnsureAppExistsNoLock(shortcutId);
        }

        IReadOnlyList<ProcessWindowInfo>? windowsBeforeLaunch = null;
        if (_processWindowService is not null)
        {
            try
            {
                windowsBeforeLaunch = _processWindowService.GetSnapshot().Windows;
            }
            catch
            {
                // Window discovery must never prevent the actual app launch.
            }
        }

        var expectedProcessName = TryResolveExpectedProcessName(app);
        Process? launchedProcess;

        if (string.Equals(app.SourceKind, AppStartSourceKinds.Packaged, StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add($"shell:AppsFolder\\{app.SourcePath}");
            launchedProcess = Process.Start(startInfo);
        }
        else
        {
            if (!File.Exists(app.SourcePath))
            {
                throw new InvalidOperationException("The selected app shortcut no longer exists. Refresh the app index.");
            }

            launchedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = app.SourcePath,
                UseShellExecute = true
            });
        }

        int? launchedProcessId = null;
        if (launchedProcess is not null)
        {
            try
            {
                launchedProcessId = launchedProcess.Id;
            }
            catch
            {
                // Shell launches do not always expose the target process ID.
            }
            finally
            {
                launchedProcess.Dispose();
            }
        }

        if (_processWindowService is not null && windowsBeforeLaunch is not null)
        {
            _processWindowService.ActivateLaunchedAppWhenReady(
                app.Name,
                expectedProcessName,
                launchedProcessId,
                windowsBeforeLaunch);
        }

        return GetSnapshot();
    }

    private static string? TryResolveExpectedProcessName(AppStartCatalogConfiguration app)
    {
        if (!string.Equals(app.SourceKind, AppStartSourceKinds.Desktop, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var targetPath = TryResolveShortcutTargetPath(app.SourcePath);
        return string.IsNullOrWhiteSpace(targetPath)
            ? null
            : Path.GetFileNameWithoutExtension(targetPath);
    }

    private static string? TryResolveShortcutTargetPath(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);
            var targetPath = shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
            return string.IsNullOrWhiteSpace(targetPath)
                ? null
                : Environment.ExpandEnvironmentVariables(targetPath.Trim());
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private AppStartCatalogSnapshot BuildCatalogSnapshotNoLock(AppStartCatalogData catalog)
    {
        var settings = LoadSettingsNoLock();
        var favorites = settings.FavoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = settings.HiddenIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = catalog.Entries
            .Select(entry => ToCatalogEntry(entry, favorites.Contains(entry.Id), hidden.Contains(entry.Id)))
            .OrderBy(entry => entry.Hidden)
            .ThenByDescending(entry => entry.Favorite)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hiddenCount = apps.Count(entry => entry.Hidden);

        return new AppStartCatalogSnapshot(
            apps,
            apps.Length switch
            {
                0 => "No launchable Windows apps were detected.",
                1 => "1 installed app indexed.",
                _ when hiddenCount > 0 => $"{apps.Length} installed apps indexed · {hiddenCount} hidden.",
                _ => $"{apps.Length} installed apps indexed."
            });
    }

    private AppStartCatalogData EnsureCatalogNoLock()
    {
        _catalog ??= LoadCatalogNoLock();
        if (_catalog.LastScanUtc == default || _utcNow() - _catalog.LastScanUtc >= AutomaticRefreshInterval)
        {
            _catalog = RefreshCatalogNoLock();
        }

        return _catalog;
    }

    private AppStartCatalogData RefreshCatalogNoLock()
    {
        _catalog ??= LoadCatalogNoLock();
        var previousById = _catalog.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var discovery = _discoverApps();
        var updated = new Dictionary<string, AppStartCatalogConfiguration>(StringComparer.OrdinalIgnoreCase);

        foreach (var discovered in discovery.Entries)
        {
            if (previousById.TryGetValue(discovered.Id, out var previous) &&
                string.Equals(previous.Fingerprint, discovered.Fingerprint, StringComparison.Ordinal))
            {
                updated[discovered.Id] = previous;
                continue;
            }

            updated[discovered.Id] = new AppStartCatalogConfiguration
            {
                Id = discovered.Id,
                Name = discovered.Name,
                SourcePath = discovered.SourcePath,
                SourceKind = discovered.SourceKind,
                Fingerprint = discovered.Fingerprint,
                IconDataUri = TryCreateShellIconDataUri(discovered.IconPath) ?? CreateIconDataUri(discovered.Name)
            };
        }

        if (!discovery.PackagedAppsScanSucceeded)
        {
            foreach (var previous in previousById.Values.Where(entry =>
                         string.Equals(entry.SourceKind, AppStartSourceKinds.Packaged, StringComparison.OrdinalIgnoreCase)))
            {
                updated.TryAdd(previous.Id, previous);
            }
        }

        _catalog = new AppStartCatalogData
        {
            LastScanUtc = _utcNow(),
            Entries = updated.Values
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        SaveJsonNoLock(_catalogPath, _catalog);
        return _catalog;
    }

    private AppStartCatalogConfiguration EnsureAppExistsNoLock(string appId)
    {
        return EnsureCatalogNoLock().Entries.FirstOrDefault(entry =>
                   string.Equals(entry.Id, appId, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("The selected app is no longer available. Refresh the app index.");
    }

    private AppStartSettingsData LoadSettingsNoLock()
    {
        AppStartSettingsData settings;
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppStartSettingsData { AutomaticCatalogEnabled = true };
            }

            settings = JsonSerializer.Deserialize<AppStartSettingsData>(File.ReadAllText(_settingsPath), JsonOptions)
                       ?? new AppStartSettingsData();
        }
        catch
        {
            return new AppStartSettingsData { AutomaticCatalogEnabled = true };
        }

        settings = settings with
        {
            FavoriteIds = settings.FavoriteIds ?? [],
            HiddenIds = settings.HiddenIds ?? [],
            Shortcuts = settings.Shortcuts ?? []
        };

        if (!settings.AutomaticCatalogEnabled)
        {
            foreach (var shortcut in settings.Shortcuts.Where(entry => !string.IsNullOrWhiteSpace(entry.Id)))
            {
                AddId(settings.FavoriteIds, shortcut.Id);
            }

            settings = settings with { AutomaticCatalogEnabled = true };
            SaveSettingsNoLock(settings);
        }

        return settings;
    }

    private AppStartCatalogData LoadCatalogNoLock()
    {
        try
        {
            if (!File.Exists(_catalogPath))
            {
                return new AppStartCatalogData();
            }

            var catalog = JsonSerializer.Deserialize<AppStartCatalogData>(File.ReadAllText(_catalogPath), JsonOptions)
                          ?? new AppStartCatalogData();
            return catalog with { Entries = catalog.Entries ?? [] };
        }
        catch
        {
            return new AppStartCatalogData();
        }
    }

    private void SaveSettingsNoLock(AppStartSettingsData settings)
    {
        SaveJsonNoLock(_settingsPath, settings);
    }

    private static void SaveJsonNoLock<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private static AppStartShortcutState ToShortcutState(AppStartCatalogConfiguration entry, bool favorite)
    {
        return new AppStartShortcutState(
            entry.Id,
            entry.Name,
            entry.SourcePath,
            entry.IconDataUri,
            favorite,
            entry.SourceKind);
    }

    private static AppStartCatalogEntry ToCatalogEntry(
        AppStartCatalogConfiguration entry,
        bool favorite,
        bool hidden)
    {
        return new AppStartCatalogEntry(
            entry.Id,
            entry.Name,
            entry.SourcePath,
            entry.IconDataUri,
            Added: !hidden,
            favorite,
            hidden,
            entry.SourceKind);
    }

    private static AppStartDiscoveryResult DiscoverInstalledApps()
    {
        var entries = new Dictionary<string, AppStartDiscoveredEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in DiscoverDesktopShortcuts())
        {
            entries[entry.Id] = entry;
        }

        var packagedAppsScanSucceeded = TryDiscoverPackagedApps(out var packagedApps);
        foreach (var entry in packagedApps)
        {
            entries.TryAdd(entry.Id, entry);
        }

        return new AppStartDiscoveryResult(entries.Values.ToArray(), packagedAppsScanSucceeded);
    }

    private static IEnumerable<AppStartDiscoveredEntry> DiscoverDesktopShortcuts()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var shortcutPath in EnumerateShortcutFiles(root))
            {
                var name = CleanShortcutName(Path.GetFileNameWithoutExtension(shortcutPath));
                if (ShouldIgnoreShortcut(name))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(shortcutPath);
                }
                catch
                {
                    continue;
                }

                yield return new AppStartDiscoveredEntry(
                    CreateStableId(shortcutPath),
                    name,
                    shortcutPath,
                    AppStartSourceKinds.Desktop,
                    $"{shortcutPath.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}",
                    shortcutPath);
            }
        }
    }

    private static bool TryDiscoverPackagedApps(out IReadOnlyList<AppStartDiscoveredEntry> entries)
    {
        entries = [];
        try
        {
            var powerShellPath = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(powerShellPath) ? powerShellPath : "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "Get-StartApps | Where-Object { $_.AppID -like '*!*' } | Select-Object Name,AppID | ConvertTo-Json -Compress");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000))
            {
                process.Kill(true);
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return process.ExitCode == 0;
            }

            using var document = JsonDocument.Parse(output);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            entries = elements
                .Select(element => new
                {
                    Name = GetJsonString(element, "Name"),
                    AppId = GetJsonString(element, "AppID")
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) &&
                                !string.IsNullOrWhiteSpace(entry.AppId) &&
                                !ShouldIgnoreShortcut(entry.Name))
                .Select(entry => new AppStartDiscoveredEntry(
                    CreateStableId($"packaged:{entry.AppId}"),
                    entry.Name,
                    entry.AppId,
                    AppStartSourceKinds.Packaged,
                    $"{entry.AppId.ToUpperInvariant()}|{entry.Name}",
                    $"shell:AppsFolder\\{entry.AppId}"))
                .ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
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
        return string.IsNullOrWhiteSpace(name) ||
               IgnoredNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
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

    private static void ValidateAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new InvalidOperationException("An app ID is required.");
        }
    }

    private static void AddId(List<string> ids, string id)
    {
        if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(id);
        }
    }

    private static bool RemoveId(List<string> ids, string id)
    {
        return ids.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)) > 0;
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
        public bool AutomaticCatalogEnabled { get; init; }

        public List<string> FavoriteIds { get; init; } = [];

        public List<string> HiddenIds { get; init; } = [];

        public List<LegacyAppStartShortcutConfiguration> Shortcuts { get; init; } = [];
    }

    private sealed record LegacyAppStartShortcutConfiguration
    {
        public string Id { get; init; } = string.Empty;
    }

    private sealed record AppStartCatalogData
    {
        public DateTimeOffset LastScanUtc { get; init; }

        public List<AppStartCatalogConfiguration> Entries { get; init; } = [];
    }

    private sealed record AppStartCatalogConfiguration
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string SourcePath { get; init; } = string.Empty;

        public string SourceKind { get; init; } = AppStartSourceKinds.Desktop;

        public string Fingerprint { get; init; } = string.Empty;

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

internal static class AppStartSourceKinds
{
    public const string Desktop = "desktop";
    public const string Packaged = "packaged";
}

internal sealed record AppStartDiscoveryResult(
    IReadOnlyList<AppStartDiscoveredEntry> Entries,
    bool PackagedAppsScanSucceeded = true);

internal sealed record AppStartDiscoveredEntry(
    string Id,
    string Name,
    string SourcePath,
    string SourceKind,
    string Fingerprint,
    string IconPath);
