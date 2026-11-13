using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record OmniLibraryRomSystemDescriptor(
    string Id,
    string Title,
    string FolderName,
    IReadOnlySet<string> Extensions,
    string LibretroPlaylistName)
{
    /// <summary>ORBIT's system id, which is also its managed ROM folder name.</summary>
    public string OrbitId { get; init; } = string.Empty;

    /// <summary>Extensions only this system uses; they win in any folder.</summary>
    public IReadOnlyList<string> UniqueExtensions { get; init; } = [];

    /// <summary>Emulators for this system, recommended one first.</summary>
    public IReadOnlyList<string> EmulatorIds { get; init; } = [];

    /// <summary>RetroArch cores in order of preference (without "_libretro").</summary>
    public IReadOnlyList<string> RetroArchCores { get; init; } = [];

    public int RetroAchievementsConsoleId { get; init; }

    public bool IsSupported => Extensions.Count > 0;

    public string EmulatorTitle =>
        RecommendedEmulator?.Title ?? string.Empty;

    public string EmulatorExecutableName =>
        RecommendedEmulator?.ExecutableNames.FirstOrDefault() ?? string.Empty;

    public OmniLibraryEmulatorDescriptor? RecommendedEmulator =>
        EmulatorIds.Count > 0 &&
        OmniLibraryEmulatorCatalog.TryGet(EmulatorIds[0], out var emulator)
            ? emulator
            : null;
}

/// <summary>
/// Canonical ROM folder and system identity registry. Folder names are stable
/// user-facing API: future releases add scanners/emulators to these descriptors
/// without guessing a ROM's platform or moving the user's files. The system
/// list, extensions, emulator choices and RetroArch cores follow ORBIT's
/// retro library.
/// </summary>
internal static class OmniLibraryRomSystemRegistry
{
    public const string StoreId = "rom-library";

    /// <summary>Prefix that keeps ROMs imported from ORBIT's folders apart from TFS' own root.</summary>
    public const string OrbitSourcePrefix = "orbit";

    /// <summary>Prefix for ROMs found through an emulator's own game folders or game list.</summary>
    public const string EmulatorListSourcePrefix = "emulator";

    private static readonly string[] Archives = [".zip", ".7z"];

    private static readonly OmniLibraryRomSystemDescriptor[] Systems =
    [
        Placeholder("3do", "3DO", "3DO"),
        Rom("arcade", "Arcade", "Arcade", "arcade", [".zip", ".7z"], [], ["mame", "retroarch"], ["fbneo", "mame"], 27, string.Empty),
        Rom("atari-2600", "Atari 2600", "Atari 2600", "atari2600", [".a26", .. Archives], [".a26"], ["retroarch"], ["stella"], 25, "Atari - 2600"),
        Placeholder("atari-5200", "Atari 5200", "Atari 5200"),
        Rom("atari-7800", "Atari 7800", "Atari 7800", "atari7800", [".a78", .. Archives], [".a78"], ["retroarch"], ["prosystem"], 51, "Atari - 7800"),
        Placeholder("atari-jaguar", "Atari Jaguar", "Atari Jaguar"),
        Rom("atari-lynx", "Atari Lynx", "Atari Lynx", "atarilynx", [".lnx", .. Archives], [".lnx"], ["retroarch"], ["handy", "mednafen_lynx"], 13, "Atari - Lynx"),
        Rom("colecovision", "ColecoVision", "ColecoVision", "colecovision", [".col", .. Archives], [".col"], ["retroarch"], ["bluemsx", "gearcoleco"], 44, "Coleco - ColecoVision"),
        Rom("dreamcast", "Dreamcast", "Dreamcast", "dreamcast", [".gdi", ".cdi", ".chd", ".m3u"], [".gdi", ".cdi"], ["flycast", "retroarch"], ["flycast"], 40, "Sega - Dreamcast"),
        Rom("famicom-disk-system", "Famicom Disk System", "Famicom Disk System", "fds", [".fds", .. Archives], [".fds"], ["retroarch"], ["mesen", "nestopia", "fceumm"], 7, "Nintendo - Family Computer Disk System"),
        Rom("game-boy", "Game Boy", "Game Boy", "gb", [".gb", .. Archives], [".gb"], ["mgba", "retroarch"], ["gambatte", "sameboy", "mgba"], 4, "Nintendo - Game Boy"),
        Rom("game-boy-color", "Game Boy Color", "Game Boy Color", "gbc", [".gbc", .. Archives], [".gbc"], ["mgba", "retroarch"], ["gambatte", "sameboy", "mgba"], 6, "Nintendo - Game Boy Color"),
        Rom("game-boy-advance", "Game Boy Advance", "Game Boy Advance", "gba", [".gba", .. Archives], [".gba"], ["mgba", "retroarch"], ["mgba", "vba_next"], 5, "Nintendo - Game Boy Advance"),
        Rom("game-gear", "Game Gear", "Game Gear", "gamegear", [".gg", .. Archives], [".gg"], ["retroarch"], ["genesis_plus_gx", "picodrive"], 15, "Sega - Game Gear"),
        Rom("gamecube", "GameCube", "GameCube", "gamecube", [".iso", ".gcm", ".rvz", ".wia", ".wbfs", ".ciso", ".gcz"], [".rvz", ".gcz", ".gcm"], ["dolphin", "retroarch"], ["dolphin"], 16, "Nintendo - GameCube"),
        Rom("master-system", "Master System", "Master System", "mastersystem", [".sms", .. Archives], [".sms"], ["retroarch"], ["genesis_plus_gx", "picodrive"], 11, "Sega - Master System - Mark III"),
        Rom("mega-drive", "Mega Drive", "Mega Drive", "megadrive", [".md", ".gen", ".smd", .. Archives], [".md", ".gen"], ["retroarch"], ["genesis_plus_gx", "picodrive"], 1, "Sega - Mega Drive - Genesis"),
        Rom("nintendo-64", "Nintendo 64", "Nintendo 64", "n64", [".z64", ".n64", ".v64", .. Archives], [".z64", ".v64", ".n64"], ["project64", "ares", "retroarch"], ["mupen64plus_next", "parallel_n64"], 2, "Nintendo - Nintendo 64"),
        Rom("nintendo-ds", "Nintendo DS", "Nintendo DS", "nds", [".nds", .. Archives], [".nds"], ["melonds", "retroarch"], ["melonds", "desmume"], 18, "Nintendo - Nintendo DS"),
        Rom("nintendo-3ds", "Nintendo 3DS", "Nintendo 3DS", "3ds", [".3ds", ".3dsx", ".cci", ".cxi"], [".3ds", ".3dsx", ".cci", ".cxi"], ["azahar"], [], 0, string.Empty),
        Rom("nes", "NES", "NES", "nes", [".nes", .. Archives], [".nes"], ["retroarch"], ["mesen", "nestopia", "fceumm"], 7, "Nintendo - Nintendo Entertainment System"),
        Placeholder("neo-geo", "Neo Geo", "Neo Geo"),
        Rom("neo-geo-pocket", "Neo Geo Pocket", "Neo Geo Pocket", "ngp", [".ngp", .. Archives], [".ngp"], ["retroarch"], ["mednafen_ngp"], 14, "SNK - Neo Geo Pocket"),
        Rom("neo-geo-pocket-color", "Neo Geo Pocket Color", "Neo Geo Pocket Color", "ngpc", [".ngc", .. Archives], [".ngc"], ["retroarch"], ["mednafen_ngp"], 14, "SNK - Neo Geo Pocket Color"),
        Rom("pc-engine", "PC Engine", "PC Engine", "pce", [".pce", .. Archives], [".pce"], ["retroarch"], ["mednafen_pce_fast", "mednafen_supergrafx"], 8, "NEC - PC Engine - TurboGrafx 16"),
        Rom("playstation", "PlayStation", "PlayStation", "ps1", [".cue", ".chd", ".m3u", ".pbp", ".iso"], [], ["duckstation", "retroarch"], ["mednafen_psx_hw", "mednafen_psx", "beetle_psx_hw", "beetle_psx", "swanstation", "pcsx_rearmed"], 12, "Sony - PlayStation"),
        Rom("playstation-2", "PlayStation 2", "PlayStation 2", "ps2", [".iso", ".chd", ".cso"], [], ["pcsx2", "retroarch"], ["pcsx2", "play"], 21, "Sony - PlayStation 2"),
        Rom("playstation-3", "PlayStation 3", "PlayStation 3", "ps3", [".iso"], [], ["rpcs3"], [], 0, "Sony - PlayStation 3"),
        Rom("psp", "PSP", "PSP", "psp", [".iso", ".cso", ".chd", ".pbp"], [".cso"], ["ppsspp", "retroarch"], ["ppsspp"], 41, "Sony - PlayStation Portable"),
        Placeholder("ps-vita", "PS Vita", "PS Vita"),
        Rom("saturn", "Saturn", "Saturn", "saturn", [".cue", ".chd", ".m3u", ".iso"], [], ["retroarch"], ["mednafen_saturn", "kronos", "beetle_saturn", "yabause"], 39, "Sega - Saturn"),
        Rom("sega-32x", "Sega 32X", "Sega 32X", "sega32x", [".32x", .. Archives], [".32x"], ["retroarch"], ["picodrive"], 10, "Sega - 32X"),
        Rom("sega-cd", "Sega CD", "Sega CD", "segacd", [".cue", ".chd", ".m3u", ".iso"], [], ["retroarch"], ["genesis_plus_gx", "picodrive"], 9, "Sega - Mega-CD - Sega CD"),
        Rom("snes", "SNES", "SNES", "snes", [".sfc", ".smc", .. Archives], [".sfc", ".smc"], ["snes9x", "retroarch"], ["snes9x", "bsnes"], 3, "Nintendo - Super Nintendo Entertainment System"),
        Rom("switch", "Switch", "Switch", "switch", [".xci", ".nsp", ".nro"], [".xci", ".nsp", ".nro"], ["eden"], [], 0, string.Empty),
        Rom("switch-2", "Switch 2", "Switch 2", "switch2", [".nup", ".xci", ".nsp"], [".nup"], ["eglantine"], [], 0, string.Empty),
        Rom("virtual-boy", "Virtual Boy", "Virtual Boy", "virtualboy", [".vb", .. Archives], [".vb"], ["retroarch"], ["mednafen_vb"], 28, "Nintendo - Virtual Boy"),
        Rom("wii", "Wii", "Wii", "wii", [".wbfs", ".wad", ".iso", ".rvz", ".wia", ".gcz", ".ciso"], [".wbfs", ".wad"], ["dolphin", "retroarch"], ["dolphin"], 19, "Nintendo - Wii"),
        Rom("wii-u", "Wii U", "Wii U", "wiiu", [".wua", ".wux", ".wud", ".rpx"], [".wua", ".wux", ".wud", ".rpx"], ["cemu"], [], 0, "Nintendo - Wii U"),
        Rom("wonderswan", "WonderSwan", "WonderSwan", "wonderswan", [".ws", .. Archives], [".ws"], ["retroarch"], ["mednafen_wswan"], 53, "Bandai - WonderSwan"),
        Rom("wonderswan-color", "WonderSwan Color", "WonderSwan Color", "wonderswancolor", [".wsc", .. Archives], [".wsc"], ["retroarch"], ["mednafen_wswan"], 53, "Bandai - WonderSwan Color"),
        Placeholder("xbox", "Xbox", "Xbox"),
        Rom("xbox-360", "Xbox 360", "Xbox 360", "xbox360", [".iso"], [], ["xenia"], [], 0, "Microsoft - Xbox 360"),
    ];

    /// <summary>
    /// MAME/FBNeo BIOS sets live next to games in an arcade folder but are not
    /// games themselves.
    /// </summary>
    private static readonly IReadOnlySet<string> ArcadeBiosSets = new HashSet<string>(
        ["neogeo", "pgm", "qsound", "cpzn1", "cpzn2", "decocass", "skns", "stvbios", "naomi", "naomi2", "awbios", "hng64", "isgsm", "nss", "megatech", "playch10"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, OmniLibraryRomSystemDescriptor> UniqueExtensionOwners =
        BuildUniqueExtensionOwners();

    public static IReadOnlyList<OmniLibraryRomSystemDescriptor> All { get; } =
        Systems;

    public static IReadOnlyList<OmniLibraryRomSystemDescriptor> Supported { get; } =
        Systems.Where(system => system.IsSupported).ToArray();

    public static bool TryGet(string? systemId, out OmniLibraryRomSystemDescriptor system)
    {
        var found = Supported.FirstOrDefault(candidate => candidate.Id.Equals(
            systemId?.Trim(),
            StringComparison.OrdinalIgnoreCase));
        system = found!;
        return found is not null;
    }

    public static OmniLibraryRomSystemDescriptor GetRequired(string systemId) =>
        TryGet(systemId, out var system)
            ? system
            : throw new InvalidOperationException($"Unsupported ROM system '{systemId}'.");

    public static OmniLibraryRomSystemDescriptor? FindByOrbitFolder(string? folderName) =>
        Supported.FirstOrDefault(system =>
            system.OrbitId.Equals(folderName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string DefaultRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Roms");

    /// <summary>ORBIT's managed ROM folder (Documents\ORBIT\ROMs\&lt;system&gt;).</summary>
    public static string OrbitRomRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ORBIT",
        "ROMs");

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

    /// <summary>
    /// Decides which system a file belongs to, the way ORBIT does: a folder
    /// keeps every extension it accepts; an extension only one system uses
    /// (for example .gb in a Game Boy Color folder, or a GameCube .rvz in a
    /// Wii U folder) still goes to its own system; anything else is skipped.
    /// </summary>
    public static OmniLibraryRomSystemDescriptor? DetectSystem(
        OmniLibraryRomSystemDescriptor folderSystem,
        string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var normalized = filePath.Replace('\\', '/');
        if (folderSystem.Id.Equals("playstation-3", StringComparison.OrdinalIgnoreCase) &&
            fileName.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(normalized, "/PS3_GAME/USRDIR/EBOOT\\.BIN$", RegexOptions.IgnoreCase)
                ? folderSystem
                : null;
        }

        if (folderSystem.Id.Equals("xbox-360", StringComparison.OrdinalIgnoreCase) &&
            fileName.Equals("default.xex", StringComparison.OrdinalIgnoreCase))
        {
            return folderSystem;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        if (folderSystem.Id.Equals("arcade", StringComparison.OrdinalIgnoreCase) &&
            ArcadeBiosSets.Contains(Path.GetFileNameWithoutExtension(filePath)))
        {
            return null;
        }

        if (folderSystem.Extensions.Contains(extension))
        {
            return folderSystem;
        }

        return UniqueExtensionOwners.TryGetValue(extension, out var owner) ? owner : null;
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

    /// <summary>
    /// The name a ROM is shown and searched under. Generic boot files take the
    /// name of their game folder (PSP EBOOT.PBP, PS3 PS3_GAME\USRDIR\EBOOT.BIN,
    /// Xbox 360 default.xex, Wii U code\*.rpx).
    /// </summary>
    public static string BuildDisplayTitle(string romPath)
    {
        var fileName = GameNameSource(romPath);
        var title = Regex.Replace(fileName, @"_+|\.(?=\S)", " ");
        // Scene release numbers such as "1435 - Yu-Gi-Oh!".
        title = Regex.Replace(title, @"^\d{3,4}\s+-\s+", string.Empty);
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
                @"\s*[\(\[](?:(?:USA|Europe|Australia|Japan|World|Asia|Korea|Germany|France|Spain|Italy|Brazil|UK|Canada|China|Russia|Netherlands|Sweden|Scandinavia)(?:,\s*(?:USA|Europe|Australia|Japan|World|Asia|Korea|Germany|France|Spain|Italy|Brazil|UK|Canada|China|Russia|Netherlands|Sweden|Scandinavia))*|(?-i:[A-Z][a-z](?:[,+][A-Z][a-z])*)|E|J|U|G|F|S|I|K|EU|UE|JU|JUE|Rev[^\)\]]*|v\d[^\)\]]*|PSP|PSN|GBA|GBC|SGB|NDS|!|b\d*|h\d*|a\d*|t\d*|o\d*|Beta[^\)\]]*|Proto[^\)\]]*|Demo|Sample|Unl|[0-9A-F]{16}|v\d+)[\)\]]\s*$",
                string.Empty,
                RegexOptions.IgnoreCase);
        }
        while (!title.Equals(previousTitle, StringComparison.Ordinal));
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '_');
        // "Legend of Zelda, The - Link's Awakening" -> "The Legend of Zelda - Link's Awakening".
        title = Regex.Replace(
            title,
            @"^(?<name>[^-]+?),\s*(?<article>The|A|An)(?<rest>\s+-\s+.*)?$",
            match => $"{match.Groups["article"].Value} {match.Groups["name"].Value}{match.Groups["rest"].Value}");
        return string.IsNullOrWhiteSpace(title) ? fileName : title;
    }

    public static string BuildLibretroBoxArtUrl(
        OmniLibraryRomSystemDescriptor system,
        string romPath)
    {
        if (string.IsNullOrWhiteSpace(system.LibretroPlaylistName))
        {
            return string.Empty;
        }

        var thumbnailName = GameNameSource(romPath);
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

    private static string GameNameSource(string romPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(romPath);
        var directory = Path.GetDirectoryName(romPath);
        var parent = Path.GetFileName(directory) ?? string.Empty;
        var extension = Path.GetExtension(romPath);
        if (fileName.Equals("EBOOT", StringComparison.OrdinalIgnoreCase) &&
            extension.Equals(".BIN", StringComparison.OrdinalIgnoreCase) &&
            parent.Equals("USRDIR", StringComparison.OrdinalIgnoreCase))
        {
            // <Game>\PS3_GAME\USRDIR\EBOOT.BIN
            var game = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(directory)));
            return string.IsNullOrWhiteSpace(game) ? fileName : game;
        }

        if (fileName.Equals("EBOOT", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(romPath).Equals("default.xex", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(parent) ? fileName : parent;
        }

        if (extension.Equals(".rpx", StringComparison.OrdinalIgnoreCase) &&
            parent.Equals("code", StringComparison.OrdinalIgnoreCase))
        {
            var game = Path.GetFileName(Path.GetDirectoryName(directory));
            return string.IsNullOrWhiteSpace(game) ? fileName : game;
        }

        return fileName;
    }

    private static string NormalizeRelativePath(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim()
            .ToLowerInvariant();

    private static IReadOnlyDictionary<string, OmniLibraryRomSystemDescriptor> BuildUniqueExtensionOwners()
    {
        var owners = new Dictionary<string, OmniLibraryRomSystemDescriptor>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in Systems)
        {
            foreach (var extension in system.UniqueExtensions)
            {
                if (!owners.TryAdd(extension, system))
                {
                    conflicts.Add(extension);
                }
            }
        }

        foreach (var conflict in conflicts)
        {
            owners.Remove(conflict);
        }

        return owners;
    }

    private static OmniLibraryRomSystemDescriptor Rom(
        string id,
        string title,
        string folderName,
        string orbitId,
        string[] extensions,
        string[] uniqueExtensions,
        string[] emulatorIds,
        string[] retroArchCores,
        int retroAchievementsConsoleId,
        string libretroPlaylistName) =>
        new(
            id,
            title,
            folderName,
            new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
            libretroPlaylistName)
        {
            OrbitId = orbitId,
            UniqueExtensions = uniqueExtensions,
            EmulatorIds = emulatorIds,
            RetroArchCores = retroArchCores,
            RetroAchievementsConsoleId = retroAchievementsConsoleId,
        };

    private static OmniLibraryRomSystemDescriptor Placeholder(
        string id,
        string title,
        string folderName) =>
        new(
            id,
            title,
            folderName,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            string.Empty);
}
