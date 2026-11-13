using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// One place an installed emulator keeps games: a game folder from its
/// settings or a single file from its game list / recent list.
/// </summary>
internal sealed record OmniLibraryEmulatorGameSource(
    string EmulatorId,
    string Path,
    bool IsDirectory,
    bool Recursive,
    IReadOnlyList<string> SystemIds);

/// <summary>
/// Reads the game folders and game lists of installed emulators (Dolphin,
/// Cemu, Eden, Eglantine, Azahar, PPSSPP, DuckStation, PCSX2, RPCS3,
/// RetroArch playlists, Flycast), so ROMs the user already set up there show
/// up in the Emulation library without moving them. Only well-known settings
/// are read; folders that are clearly not game folders (drive roots, Windows,
/// the user profile itself) are ignored.
/// </summary>
internal static class OmniLibraryEmulatorGameLists
{
    private const long MaximumConfigBytes = 4L * 1024 * 1024;
    private const int MaximumDirectories = 24;
    private const int MaximumFilesPerDirectory = 20_000;
    private static readonly string[] SwitchDirectoryPlaceholders = ["SDMC", "UserNAND", "SysNAND"];

    public static IReadOnlyList<OmniLibraryEmulatorGameSource> Discover()
    {
        var sources = new List<OmniLibraryEmulatorGameSource>();
        foreach (var emulator in OmniLibraryEmulatorCatalog.All)
        {
            foreach (var directory in ExecutableDirectories(emulator))
            try
            {
                sources.AddRange(emulator.Id switch
                {
                    "dolphin" => Dolphin(directory),
                    "cemu" => Cemu(directory),
                    "eden" => QtGameDirectories(emulator.Id, directory, "eden", "switch"),
                    "eglantine" => QtGameDirectories(emulator.Id, directory, "eglantine", "switch-2"),
                    "azahar" => QtGameDirectories(emulator.Id, directory, "Azahar", "nintendo-3ds"),
                    "ppsspp" => Ppsspp(directory),
                    "duckstation" => GameListIni(emulator.Id, "playstation", [
                        Join(directory, "settings.ini"),
                        Join(Documents, "DuckStation", "settings.ini"),
                        Join(LocalAppData, "DuckStation", "settings.ini"),
                    ]),
                    "pcsx2" => GameListIni(emulator.Id, "playstation-2", [
                        Join(directory, "inis", "PCSX2.ini"),
                        Join(Documents, "PCSX2", "inis", "PCSX2.ini"),
                    ]),
                    "rpcs3" => Rpcs3(directory),
                    OmniLibraryEmulatorCatalog.RetroArchId => RetroArch(directory),
                    "flycast" => Flycast(directory),
                    _ => [],
                });
            }
            catch
            {
                // One unreadable emulator configuration never blocks the scan.
            }
        }

        var directories = 0;
        return sources
            .Where(source => IsUsablePath(source.Path, source.IsDirectory))
            .GroupBy(source => $"{source.IsDirectory}|{Path.GetFullPath(source.Path)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(source => !source.IsDirectory || ++directories <= MaximumDirectories)
            .ToArray();
    }

    /// <summary>
    /// Folders of every copy of an emulator TFS knows (its own, ORBIT's and
    /// the detected one), plus "" so per-user settings folders are read even
    /// when no executable was found.
    /// </summary>
    private static IEnumerable<string> ExecutableDirectories(OmniLibraryEmulatorDescriptor emulator)
    {
        var executables = new[]
        {
            OmniLibraryEmulatorDetector.NestedExecutable(
                [OmniLibraryEmulatorDetector.ManagedDirectory(emulator.Id)],
                emulator.ExecutableNames),
            OmniLibraryEmulatorDetector.NestedExecutable(
                [OmniLibraryEmulatorDetector.OrbitManagedDirectory(emulator.Id)],
                emulator.ExecutableNames),
            OmniLibraryEmulatorDetector.Locate(emulator),
        };
        var directories = executables
            .Where(path => path.Length > 0)
            .Select(path => Path.GetDirectoryName(path) ?? string.Empty)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Per-user settings (AppData/Documents) are read once, with the first folder.
        return directories.Count > 0 ? directories : [string.Empty];
    }

    /// <summary>The ROM files of one source with the system each belongs to.</summary>
    public static IEnumerable<(OmniLibraryRomSystemDescriptor System, string Path)> EnumerateGames(
        OmniLibraryEmulatorGameSource source,
        Func<string, IEnumerable<string>> enumerateDirectory,
        Func<IReadOnlyList<string>, ISet<string>> playlistMembers)
    {
        var systems = source.SystemIds
            .Select(id => OmniLibraryRomSystemRegistry.TryGet(id, out var system) ? system : null)
            .OfType<OmniLibraryRomSystemDescriptor>()
            .ToArray();
        if (systems.Length == 0)
        {
            yield break;
        }

        if (!source.IsDirectory)
        {
            // RPCS3's game list points at extracted disc folders directly.
            var system = source.EmulatorId == "rpcs3"
                ? systems[0]
                : DetectForSource(systems, source.Path);
            if (system is not null)
            {
                yield return (system, Path.GetFullPath(source.Path));
            }

            yield break;
        }

        var files = enumerateDirectory(source.Path)
            .Take(MaximumFilesPerDirectory)
            .ToArray();
        var members = playlistMembers(files);
        foreach (var file in files)
        {
            if (members.Contains(file) || DetectForSource(systems, file) is not { } system)
            {
                continue;
            }

            yield return (system, file);
        }
    }

    internal static OmniLibraryRomSystemDescriptor? DetectForSource(
        IReadOnlyList<OmniLibraryRomSystemDescriptor> systems,
        string path)
    {
        if (systems.Count == 1)
        {
            return OmniLibraryRomSystemRegistry.DetectSystem(systems[0], path);
        }

        // Dolphin lists GameCube and Wii games together; the disc ID decides.
        if (systems.Any(system => system.Id == "gamecube") && systems.Any(system => system.Id == "wii"))
        {
            var gameCube = systems.First(system => system.Id == "gamecube");
            var wii = systems.First(system => system.Id == "wii");
            var extension = Path.GetExtension(path);
            if (!gameCube.Extensions.Contains(extension) && !wii.Extensions.Contains(extension))
            {
                return null;
            }

            return DolphinDiscPlatform(path) switch
            {
                "wii" => wii,
                "gamecube" => gameCube,
                _ => extension.Equals(".wbfs", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".wad", StringComparison.OrdinalIgnoreCase)
                    ? wii
                    : gameCube,
            };
        }

        return systems
            .Select(system => OmniLibraryRomSystemRegistry.DetectSystem(system, path))
            .FirstOrDefault(system => system is not null && systems.Contains(system));
    }

    /// <summary>"gamecube"/"wii" from the disc ID's first letter, or null.</summary>
    internal static string? DolphinDiscPlatform(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var magic = new byte[4];
            if (stream.Read(magic, 0, 4) < 4)
            {
                return null;
            }

            var header = System.Text.Encoding.ASCII.GetString(magic);
            long offset = header switch
            {
                "RVZ\u0001" or "WIA\u0001" => 0x58,
                "WBFS" => 0x200,
                "CISO" => 0x8000,
                _ => 0,
            };
            var id = new byte[6];
            stream.Position = offset;
            if (stream.Read(id, 0, 6) < 6)
            {
                return null;
            }

            var text = System.Text.Encoding.ASCII.GetString(id);
            if (!Regex.IsMatch(text, "^[A-Z0-9]{6}$"))
            {
                return null;
            }

            return text[0] switch
            {
                'G' or 'D' or 'P' => "gamecube",
                'R' or 'S' or 'H' or 'W' => "wii",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> Dolphin(string directory)
    {
        foreach (var config in ExistingFiles(
                     Join(directory, "User", "Config", "Dolphin.ini"),
                     Join(AppData, "Dolphin Emulator", "Config", "Dolphin.ini"),
                     Join(Documents, "Dolphin Emulator", "Config", "Dolphin.ini")))
        {
            var ini = ReadIni(config);
            var general = ini.GetValueOrDefault("General") ?? [];
            var recursive = general.FirstOrDefault(entry => entry.Key.Equals("RecursiveISOPaths", StringComparison.OrdinalIgnoreCase))
                .Value?.Equals("True", StringComparison.OrdinalIgnoreCase) == true;
            foreach (var (key, value) in general)
            {
                if (Regex.IsMatch(key, "^ISOPath\\d+$", RegexOptions.IgnoreCase))
                {
                    yield return new("dolphin", NormalizePath(value), true, recursive, ["gamecube", "wii"]);
                }
            }
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> Cemu(string directory)
    {
        foreach (var config in ExistingFiles(
                     Join(directory, "portable", "settings.xml"),
                     Join(directory, "settings.xml"),
                     Join(AppData, "Cemu", "settings.xml")))
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(ReadText(config));
            }
            catch
            {
                continue;
            }

            foreach (var entry in document.Descendants("GamePaths").Elements("Entry"))
            {
                yield return new("cemu", NormalizePath(entry.Value), true, true, ["wii-u"]);
            }

            foreach (var entry in document.Descendants("RecentLaunchFiles").Elements("Entry"))
            {
                yield return new("cemu", NormalizePath(entry.Value), false, false, ["wii-u"]);
            }
        }
    }

    /// <summary>Game directories of yuzu-family and Citra-family Qt frontends.</summary>
    private static IEnumerable<OmniLibraryEmulatorGameSource> QtGameDirectories(
        string emulatorId,
        string directory,
        string appDataFolder,
        string systemId)
    {
        foreach (var config in ExistingFiles(
                     Join(directory, "user", "config", "qt-config.ini"),
                     Join(AppData, appDataFolder, "config", "qt-config.ini")))
        {
            var values = ReadIni(config)
                .SelectMany(section => section.Value)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                var match = Regex.Match(key, "^Paths\\\\gamedirs\\\\(\\d+)\\\\path$", RegexOptions.IgnoreCase);
                if (!match.Success ||
                    SwitchDirectoryPlaceholders.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var deep = values.GetValueOrDefault($"Paths\\gamedirs\\{match.Groups[1].Value}\\deep_scan");
                yield return new(
                    emulatorId,
                    NormalizePath(value),
                    true,
                    deep?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                    [systemId]);
            }

            if (values.GetValueOrDefault("Paths\\recentFiles") is { Length: > 0 } recent &&
                !recent.StartsWith('@'))
            {
                foreach (var file in recent.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return new(emulatorId, NormalizePath(file.Trim('"')), false, false, [systemId]);
                }
            }
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> Ppsspp(string directory)
    {
        foreach (var config in ExistingFiles(
                     Join(directory, "memstick", "PSP", "SYSTEM", "ppsspp.ini"),
                     Join(Documents, "PPSSPP", "PSP", "SYSTEM", "ppsspp.ini")))
        {
            // Only the recent list: CurrentDirectory is just the last folder
            // the file browser showed and may be anything.
            foreach (var (key, value) in ReadIni(config).GetValueOrDefault("Recent") ?? [])
            {
                if (Regex.IsMatch(key, "^FileName\\d+$", RegexOptions.IgnoreCase))
                {
                    yield return new("ppsspp", NormalizePath(value), false, false, ["psp"]);
                }
            }
        }
    }

    /// <summary>DuckStation and PCSX2 share the [GameList] Paths/RecursivePaths layout.</summary>
    private static IEnumerable<OmniLibraryEmulatorGameSource> GameListIni(
        string emulatorId,
        string systemId,
        IReadOnlyList<string> candidates)
    {
        foreach (var config in ExistingFiles(candidates.ToArray()))
        {
            foreach (var (key, value) in ReadIni(config).GetValueOrDefault("GameList") ?? [])
            {
                if (key.Equals("RecursivePaths", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Paths", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new(
                        emulatorId,
                        NormalizePath(value),
                        true,
                        key.Equals("RecursivePaths", StringComparison.OrdinalIgnoreCase),
                        [systemId]);
                }
            }
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> Rpcs3(string directory)
    {
        foreach (var config in ExistingFiles(Join(directory, "config", "games.yml"), Join(directory, "games.yml")))
        {
            foreach (var line in ReadText(config).Split('\n'))
            {
                var match = Regex.Match(line, "^\\s*[A-Z0-9_]{4,}\\s*:\\s*(.+?)\\s*$");
                if (!match.Success)
                {
                    continue;
                }

                var folder = NormalizePath(match.Groups[1].Value.Trim('"', '\''));
                var eboot = Join(folder, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                if (eboot.Length > 0 && File.Exists(eboot))
                {
                    yield return new("rpcs3", eboot, false, false, ["playstation-3"]);
                }
            }
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> RetroArch(string directory)
    {
        var bySystem = OmniLibraryRomSystemRegistry.Supported
            .Where(system => !string.IsNullOrWhiteSpace(system.LibretroPlaylistName))
            .GroupBy(system => system.LibretroPlaylistName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var playlists in new[] { Join(directory, "playlists"), Join(AppData, "RetroArch", "playlists") }
                     .Where(path => path.Length > 0 && Directory.Exists(path)))
        {
            foreach (var playlist in Directory.EnumerateFiles(playlists, "*.lpl").Take(100))
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(ReadText(playlist));
                }
                catch
                {
                    continue;
                }

                using (document)
                {
                    if (!document.RootElement.TryGetProperty("items", out var items) ||
                        items.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var playlistSystem = bySystem.GetValueOrDefault(Path.GetFileNameWithoutExtension(playlist));
                    foreach (var item in items.EnumerateArray())
                    {
                        var path = item.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                            ? pathElement.GetString() ?? string.Empty
                            : string.Empty;
                        var database = item.TryGetProperty("db_name", out var dbElement) && dbElement.ValueKind == JsonValueKind.String
                            ? Path.GetFileNameWithoutExtension(dbElement.GetString() ?? string.Empty)
                            : string.Empty;
                        var system = bySystem.GetValueOrDefault(database) ?? playlistSystem;
                        // "archive.zip#member.sfc" entries launch the archive.
                        var archiveMember = path.IndexOf('#');
                        if (archiveMember > 0)
                        {
                            path = path[..archiveMember];
                        }

                        if (system is not null && path.Length > 0)
                        {
                            yield return new(OmniLibraryEmulatorCatalog.RetroArchId, NormalizePath(path), false, false, [system.Id]);
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<OmniLibraryEmulatorGameSource> Flycast(string directory)
    {
        foreach (var config in ExistingFiles(Join(directory, "emu.cfg"), Join(AppData, "flycast", "emu.cfg")))
        {
            foreach (var (key, value) in ReadIni(config).GetValueOrDefault("config") ?? [])
            {
                if (!key.Equals("Dreamcast.ContentPath", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var path in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return new("flycast", NormalizePath(path), true, true, ["dreamcast"]);
                }
            }
        }
    }

    /// <summary>Sections with their key/value lines; repeated keys are kept.</summary>
    internal static Dictionary<string, List<KeyValuePair<string, string>>> ReadIni(string path)
    {
        var result = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var section = result[string.Empty] = [];
        foreach (var raw in ReadText(path).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out section!))
                {
                    section = result[name] = [];
                }

                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                section.Add(new(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }

        return result;
    }

    private static bool IsUsablePath(string path, bool isDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!isDirectory)
            {
                return File.Exists(full);
            }

            if (!Directory.Exists(full) || Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            {
                return false;
            }

            var blocked = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            return !blocked.Any(blockedPath =>
                !string.IsNullOrWhiteSpace(blockedPath) &&
                blockedPath.TrimEnd(Path.DirectorySeparatorChar).Equals(full, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string ReadText(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumConfigBytes)
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> ExistingFiles(params string[] candidates) =>
        candidates
            .Where(path => path.Length > 0 && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string value)
    {
        var trimmed = value.Trim().Trim('"');
        try
        {
            return trimmed.Length == 0 ? string.Empty : Path.GetFullPath(trimmed.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Join(string root, params string[] parts) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine([root, .. parts]);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
