using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record OmniLibraryRomSystemDescriptor(
    string Id,
    string Title,
    string FolderName,
    string EmulatorTitle,
    string EmulatorExecutableName,
    IReadOnlySet<string> Extensions,
    string LibretroPlaylistName)
{
    public bool IsSupported => Extensions.Count > 0;
}

/// <summary>
/// Canonical ROM folder and system identity registry. Folder names are stable
/// user-facing API: future releases add scanners/emulators to these descriptors
/// without guessing a ROM's platform or moving the user's files.
/// </summary>
internal static class OmniLibraryRomSystemRegistry
{
    public const string StoreId = "rom-library";

    private static readonly OmniLibraryRomSystemDescriptor[] Systems =
    [
        Placeholder("3do", "3DO", "3DO"),
        Placeholder("arcade", "Arcade", "Arcade"),
        Placeholder("atari-2600", "Atari 2600", "Atari 2600"),
        Placeholder("atari-5200", "Atari 5200", "Atari 5200"),
        Placeholder("atari-7800", "Atari 7800", "Atari 7800"),
        Placeholder("atari-jaguar", "Atari Jaguar", "Atari Jaguar"),
        Placeholder("atari-lynx", "Atari Lynx", "Atari Lynx"),
        Placeholder("dreamcast", "Dreamcast", "Dreamcast"),
        Placeholder("game-boy", "Game Boy", "Game Boy"),
        Placeholder("game-boy-color", "Game Boy Color", "Game Boy Color"),
        new(
            "game-boy-advance",
            "Game Boy Advance",
            "Game Boy Advance",
            "mGBA",
            "mGBA.exe",
            new HashSet<string>([".gba"], StringComparer.OrdinalIgnoreCase),
            "Nintendo - Game Boy Advance"),
        new(
            "gamecube",
            "GameCube",
            "GameCube",
            "Dolphin",
            "Dolphin.exe",
            new HashSet<string>(
                [".iso", ".gcm", ".rvz", ".wia", ".wbfs", ".ciso", ".gcz"],
                StringComparer.OrdinalIgnoreCase),
            "Nintendo - GameCube"),
        Placeholder("master-system", "Master System", "Master System"),
        Placeholder("mega-drive", "Mega Drive", "Mega Drive"),
        new(
            "nintendo-64",
            "Nintendo 64",
            "Nintendo 64",
            "ares",
            "ares.exe",
            new HashSet<string>([".z64", ".n64", ".v64"], StringComparer.OrdinalIgnoreCase),
            "Nintendo - Nintendo 64"),
        Placeholder("nintendo-ds", "Nintendo DS", "Nintendo DS"),
        Placeholder("nintendo-3ds", "Nintendo 3DS", "Nintendo 3DS"),
        Placeholder("nes", "NES", "NES"),
        Placeholder("neo-geo", "Neo Geo", "Neo Geo"),
        Placeholder("playstation", "PlayStation", "PlayStation"),
        Placeholder("playstation-2", "PlayStation 2", "PlayStation 2"),
        Placeholder("playstation-3", "PlayStation 3", "PlayStation 3"),
        new(
            "psp",
            "PSP",
            "PSP",
            "PPSSPP",
            "PPSSPPWindows64.exe",
            new HashSet<string>(
                [".iso", ".cso", ".chd", ".pbp"],
                StringComparer.OrdinalIgnoreCase),
            "Sony - PlayStation Portable"),
        Placeholder("ps-vita", "PS Vita", "PS Vita"),
        Placeholder("saturn", "Saturn", "Saturn"),
        Placeholder("snes", "SNES", "SNES"),
        Placeholder("wii", "Wii", "Wii"),
        Placeholder("wii-u", "Wii U", "Wii U"),
        Placeholder("xbox", "Xbox", "Xbox"),
        Placeholder("xbox-360", "Xbox 360", "Xbox 360"),
    ];

    public static IReadOnlyList<OmniLibraryRomSystemDescriptor> All { get; } =
        Systems;

    public static IReadOnlyList<OmniLibraryRomSystemDescriptor> Supported { get; } =
        new[] { "psp", "gamecube", "game-boy-advance", "nintendo-64" }
            .Select(id => Systems.Single(system => system.Id.Equals(
                id,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    public static OmniLibraryRomSystemDescriptor GetRequired(string systemId) =>
        Supported.FirstOrDefault(system => system.Id.Equals(
            systemId?.Trim(),
            StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidOperationException($"Unsupported ROM system '{systemId}'.");

    public static string DefaultRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Roms");

    public static string ResolveRootPath(string? configuredPath)
    {
        var requested = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRootPath
            : configuredPath.Trim();
        return Path.GetFullPath(requested);
    }

    public static string EnsureFolderStructure(string? configuredPath)
    {
        var root = ResolveRootPath(configuredPath);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "BIOS"));
        foreach (var system in Systems)
        {
            Directory.CreateDirectory(Path.Combine(root, system.FolderName));
        }
        return root;
    }

    public static string BuildStableGameId(
        OmniLibraryRomSystemDescriptor system,
        string relativePath)
    {
        var identity = $"{system.Id}\n{NormalizeRelativePath(relativePath)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"{system.Id}:{hash[..24].ToLowerInvariant()}";
    }

    public static string BuildLibraryTabId(string? systemId)
    {
        var normalized = Regex.Replace(
                systemId?.Trim().ToLowerInvariant() ?? string.Empty,
                @"[^a-z0-9-]+",
                "-")
            .Trim('-');
        return $"tfs-emulation-{(string.IsNullOrWhiteSpace(normalized) ? "system" : normalized)}";
    }

    public static string BuildFileFingerprint(FileInfo file)
    {
        return $"{file.Length:x16}:{file.LastWriteTimeUtc.Ticks:x16}";
    }

    public static string BuildOptionalFileFingerprint(string? fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl) ||
            !Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) ||
            !uri.IsFile)
        {
            return string.Empty;
        }

        try
        {
            var file = new FileInfo(uri.LocalPath);
            return file.Exists ? BuildFileFingerprint(file) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string BuildDisplayTitle(string romPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(romPath);
        if (fileName.Equals("EBOOT", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.GetFileName(Path.GetDirectoryName(romPath)) ?? fileName;
        }

        var title = Regex.Replace(fileName, @"[_\.]+", " ");
        // No-Intro/Redump names frequently end in several independent tags,
        // for example "(Europe) (En,Fr,De)". Remove all trailing catalog tags,
        // not only the final one, so Steam and SteamGridDB receive the actual
        // title instead of a region-specific filename.
        string previousTitle;
        do
        {
            previousTitle = title;
            title = Regex.Replace(
                title,
                @"\s*[\(\[](?:USA|Europe(?:,\s*Australia)?|Australia|Japan|World|Asia|Korea|En|En,[^\)\]]+|Rev[^\)\]]*|v\d[^\)\]]*)[\)\]]\s*$",
                string.Empty,
                RegexOptions.IgnoreCase);
        }
        while (!title.Equals(previousTitle, StringComparison.Ordinal));
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '_');
        return string.IsNullOrWhiteSpace(title) ? fileName : title;
    }

    public static string BuildLibretroBoxArtUrl(
        OmniLibraryRomSystemDescriptor system,
        string romPath)
    {
        var thumbnailName = Path.GetFileNameWithoutExtension(romPath);
        if (thumbnailName.Equals("EBOOT", StringComparison.OrdinalIgnoreCase))
        {
            thumbnailName = Path.GetFileName(Path.GetDirectoryName(romPath)) ?? thumbnailName;
        }

        // RetroArch replaces filename characters which are not portable across
        // supported filesystems. Keep region/revision tags: Libretro uses them
        // to distinguish the original platform covers.
        thumbnailName = Regex.Replace(thumbnailName, @"[&\*/:`<>\?\\\|]", "_");
        return $"https://thumbnails.libretro.com/" +
               $"{Uri.EscapeDataString(system.LibretroPlaylistName)}/Named_Boxarts/" +
               $"{Uri.EscapeDataString(thumbnailName)}.png";
    }

    public static string? FindSidecarCover(string romPath)
    {
        var directory = Path.GetDirectoryName(romPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(romPath);
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, stem + ".png"),
                     Path.Combine(directory, stem + ".jpg"),
                     Path.Combine(directory, stem + ".jpeg"),
                     Path.Combine(directory, "cover.png"),
                     Path.Combine(directory, "cover.jpg"),
                 })
        {
            if (File.Exists(candidate))
            {
                return new Uri(Path.GetFullPath(candidate)).AbsoluteUri;
            }
        }
        return null;
    }

    private static string NormalizeRelativePath(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim()
            .ToLowerInvariant();

    private static OmniLibraryRomSystemDescriptor Placeholder(
        string id,
        string title,
        string folderName) =>
        new(
            id,
            title,
            folderName,
            string.Empty,
            string.Empty,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            string.Empty);
}
