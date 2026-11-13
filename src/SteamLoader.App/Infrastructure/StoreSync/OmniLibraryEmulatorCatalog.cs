using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal enum OmniLibraryEmulatorPackageKind
{
    None,
    GithubRelease,
    RetroArchStable,
    DolphinStable,
    Project64Stable,
    DirectPackage,
}

/// <summary>
/// Where an emulator's official Windows x64 package comes from. Repository
/// identities, asset selectors and pinned checksums are fixed in code; nothing
/// from the Quick Access UI can choose a download URL or an executable.
/// </summary>
internal sealed record OmniLibraryEmulatorPackage(
    OmniLibraryEmulatorPackageKind Kind,
    string Repository = "",
    Regex? AssetPattern = null,
    string Version = "",
    string Sha256 = "",
    string Url = "",
    string FileName = "")
{
    public static OmniLibraryEmulatorPackage None { get; } =
        new(OmniLibraryEmulatorPackageKind.None);
}

internal sealed record OmniLibraryEmulatorDescriptor(
    string Id,
    string Title,
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<string> InstallFolderNames,
    OmniLibraryEmulatorPackage Package)
{
    public string WingetPackageId { get; init; } = string.Empty;

    /// <summary>Early-stage project: the UI shows a compatibility note.</summary>
    public bool Experimental { get; init; }

    /// <summary>Systems this emulator only runs with user-supplied BIOS/firmware.</summary>
    public IReadOnlyList<string> FirmwareSystemIds { get; init; } = [];

    public bool CanInstall => Package.Kind != OmniLibraryEmulatorPackageKind.None;

    public bool NeedsFirmware(string systemId) =>
        FirmwareSystemIds.Contains(systemId, StringComparer.OrdinalIgnoreCase);

    public bool IsExpectedExecutable(string? path)
    {
        var fileName = Path.GetFileName(path ?? string.Empty);
        return !string.IsNullOrWhiteSpace(fileName) &&
               ExecutableNames.Any(name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Emulator catalog ported from ORBIT's retro setup: official packages,
/// expected executables, portable-mode markers and the launch contract of
/// every emulator OmniLibrary can install, detect and start.
/// </summary>
internal static class OmniLibraryEmulatorCatalog
{
    public const string RetroArchId = "retroarch";

    private static readonly OmniLibraryEmulatorDescriptor[] Emulators =
    [
        new(
            RetroArchId,
            "RetroArch",
            ["retroarch.exe"],
            ["RetroArch-Win64", "RetroArch"],
            new(
                OmniLibraryEmulatorPackageKind.RetroArchStable,
                Repository: "libretro/RetroArch",
                Version: "1.22.2",
                Sha256: "b2139b1d0f9d4526dc6b5ce23cbb3efdc766096fa6f2c3df016818b486ac6372"))
        {
            WingetPackageId = "Libretro.RetroArch",
            FirmwareSystemIds = ["famicom-disk-system", "sega-cd", "saturn", "dreamcast", "playstation", "playstation-2"],
        },
        new(
            "duckstation",
            "DuckStation",
            ["duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt-x64-ReleaseLTCG-SSE2.exe", "duckstation-qt.exe"],
            ["DuckStation"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "stenzek/duckstation",
                AssetPattern: new Regex("^duckstation-windows-x64-release\\.zip$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "Stenzek.DuckStation",
            FirmwareSystemIds = ["playstation"],
        },
        new(
            "pcsx2",
            "PCSX2",
            ["pcsx2-qt.exe", "pcsx2.exe"],
            ["PCSX2"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "PCSX2/pcsx2",
                AssetPattern: new Regex("^pcsx2-v[\\w.-]+-windows-x64-Qt\\.7z$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "PCSX2Team.PCSX2",
            FirmwareSystemIds = ["playstation-2"],
        },
        new(
            "rpcs3",
            "RPCS3",
            ["rpcs3.exe"],
            ["RPCS3"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "RPCS3/rpcs3-binaries-win",
                AssetPattern: new Regex("^rpcs3-v[\\w.-]+_win64(?:_msvc)?\\.7z$", RegexOptions.IgnoreCase)))
        {
            FirmwareSystemIds = ["playstation-3"],
        },
        new(
            "dolphin",
            "Dolphin",
            ["Dolphin.exe"],
            ["Dolphin", "Dolphin Emulator"],
            new(
                OmniLibraryEmulatorPackageKind.DolphinStable,
                Repository: "dolphin-emu/dolphin",
                Version: "2606a",
                Sha256: "4c58045f9821cb63913f4df08ea86ece3cdda9f9e646154516000fa1547e0c37"))
        {
            WingetPackageId = "DolphinEmulator.Dolphin",
        },
        new(
            "ppsspp",
            "PPSSPP",
            ["PPSSPPWindows64.exe"],
            ["PPSSPP"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "hrydgard/ppsspp",
                AssetPattern: new Regex("^PPSSPP-v[\\w.-]+-Windows-x64\\.zip$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "PPSSPPTeam.PPSSPP",
        },
        new(
            "cemu",
            "Cemu",
            ["Cemu.exe"],
            ["Cemu"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "cemu-project/Cemu",
                AssetPattern: new Regex("^cemu-[\\w.-]+-windows-x64\\.zip$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "Cemu.Cemu",
        },
        new(
            "xenia",
            "Xenia Canary",
            ["xenia_canary.exe", "xenia-canary.exe", "xenia.exe"],
            ["Xenia", "Xenia Canary"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "xenia-canary/xenia-canary",
                AssetPattern: new Regex("^xenia_canary_windows\\.7z$", RegexOptions.IgnoreCase))),
        new(
            "eden",
            "Eden",
            ["eden.exe"],
            ["Eden"],
            new(
                OmniLibraryEmulatorPackageKind.DirectPackage,
                Url: "https://stable.eden-emu.dev/v0.2.1/Eden-Windows-v0.2.1-amd64-clang-pgo.zip",
                FileName: "Eden-Windows-v0.2.1-amd64-clang-pgo.zip"))
        {
            FirmwareSystemIds = ["switch"],
        },
        new(
            "eglantine",
            "Eglantine",
            ["eglantine.exe"],
            ["Eglantine"],
            // releases/latest skips the daily and branch pre-releases.
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "Eglantine-Community/Eglantine",
                AssetPattern: new Regex("^eglantine-windows-x86_64-pgo\\.zip$", RegexOptions.IgnoreCase)))
        {
            Experimental = true,
            FirmwareSystemIds = ["switch-2"],
        },
        new(
            "azahar",
            "Azahar",
            ["azahar.exe"],
            ["Azahar"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "azahar-emu/azahar",
                AssetPattern: new Regex("^azahar-windows-msys2-[\\w.-]+\\.zip$", RegexOptions.IgnoreCase)))
        {
            FirmwareSystemIds = ["nintendo-3ds"],
        },
        new(
            "mgba",
            "mGBA",
            ["mGBA.exe"],
            ["mGBA"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "mgba-emu/mgba",
                AssetPattern: new Regex("^mGBA-[\\w.-]+-win64\\.7z$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "JeffreyPfau.mGBA",
        },
        new(
            "melonds",
            "melonDS",
            ["melonDS.exe"],
            ["melonDS"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "melonDS-emu/melonDS",
                AssetPattern: new Regex("^melonDS-[\\w.-]+-windows-x86_64\\.zip$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "melonDS.melonDS",
        },
        new(
            "snes9x",
            "Snes9x",
            ["snes9x-x64.exe", "snes9x.exe"],
            ["Snes9x"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "snes9xgit/snes9x",
                AssetPattern: new Regex("^snes9x-[\\w.-]+-win32-x64\\.zip$", RegexOptions.IgnoreCase))),
        new(
            "project64",
            "Project64",
            ["Project64.exe"],
            ["Project64 3.0", "Project64"],
            new(
                OmniLibraryEmulatorPackageKind.Project64Stable,
                Repository: "project64/project64",
                Version: "3.0.1-5664-2df3434",
                Sha256: "f8be471f105e844e32589d21f3c1ee466d5ff4d93b8b7aff69e1badf511faf7f"))
        {
            WingetPackageId = "Project64.Project64",
        },
        // Earlier TFS builds launched Nintendo 64 games with ares. It stays
        // supported for existing installations but is not installed by TFS.
        new(
            "ares",
            "ares",
            ["ares.exe"],
            ["ares"],
            OmniLibraryEmulatorPackage.None),
        new(
            "flycast",
            "Flycast",
            ["flycast.exe"],
            ["Flycast"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "flyinghead/flycast",
                AssetPattern: new Regex("^flycast-win64-[\\w.-]+\\.zip$", RegexOptions.IgnoreCase)))
        {
            FirmwareSystemIds = ["dreamcast"],
        },
        new(
            "mame",
            "MAME",
            ["mame.exe"],
            ["MAME"],
            new(
                OmniLibraryEmulatorPackageKind.GithubRelease,
                Repository: "mamedev/mame",
                AssetPattern: new Regex("^mame\\d+b_x64\\.exe$", RegexOptions.IgnoreCase)))
        {
            WingetPackageId = "MAMEdev.MAME",
        },
    ];

    private static readonly IReadOnlyDictionary<string, OmniLibraryEmulatorDescriptor> ById =
        Emulators.ToDictionary(emulator => emulator.Id, StringComparer.OrdinalIgnoreCase);

    internal static readonly IReadOnlySet<string> TrustedDownloadHosts = new HashSet<string>(
        [
            "api.github.com",
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com",
            "buildbot.libretro.com",
            "dl.dolphin-emu.org",
            "www.pj64-emu.com",
            "stable.eden-emu.dev",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<OmniLibraryEmulatorDescriptor> All { get; } = Emulators;

    public static bool TryGet(string? emulatorId, out OmniLibraryEmulatorDescriptor emulator)
    {
        if (!string.IsNullOrWhiteSpace(emulatorId) &&
            ById.TryGetValue(emulatorId.Trim(), out var found))
        {
            emulator = found;
            return true;
        }

        emulator = null!;
        return false;
    }

    public static OmniLibraryEmulatorDescriptor GetRequired(string emulatorId) =>
        TryGet(emulatorId, out var emulator)
            ? emulator
            : throw new InvalidOperationException($"Unknown emulator '{emulatorId}'.");

    /// <summary>Emulators for a system, recommended one first.</summary>
    public static IReadOnlyList<OmniLibraryEmulatorDescriptor> ForSystem(
        OmniLibraryRomSystemDescriptor system) =>
        system.EmulatorIds
            .Select(id => TryGet(id, out var emulator) ? emulator : null)
            .OfType<OmniLibraryEmulatorDescriptor>()
            .ToArray();

    /// <summary>The emulator whose executable name matches a user-selected path.</summary>
    public static OmniLibraryEmulatorDescriptor? MatchExecutable(
        OmniLibraryRomSystemDescriptor system,
        string? executablePath) =>
        ForSystem(system).FirstOrDefault(emulator => emulator.IsExpectedExecutable(executablePath));

    public static string ValidatedDownloadUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !TrustedDownloadHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException("Untrusted emulator download URL.");
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// The complete argv for one ROM. Fullscreen is the default for a
    /// controller-first launch; config-driven emulators (Project64, Snes9x)
    /// get their fullscreen preference written by <see cref="PrepareFullscreenConfiguration"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildLaunchArguments(
        string emulatorId,
        string romPath,
        string? corePath,
        bool fullscreen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        var rom = Path.GetFullPath(romPath);
        switch (emulatorId.Trim().ToLowerInvariant())
        {
            case RetroArchId:
                if (string.IsNullOrWhiteSpace(corePath))
                {
                    throw new InvalidOperationException("No compatible RetroArch core is installed.");
                }

                return fullscreen ? ["-f", "-L", corePath, rom] : ["-L", corePath, rom];
            case "ppsspp":
                return fullscreen
                    ? ["--fullscreen", "--pause-menu-exit", rom]
                    : ["--pause-menu-exit", rom];
            case "dolphin":
                return fullscreen
                    ? ["--batch", "--config=Dolphin.Display.Fullscreen=True", "--exec", rom]
                    : ["--batch", "--exec", rom];
            case "mgba":
            case "melonds":
            case "azahar":
                return fullscreen ? ["-f", rom] : [rom];
            case "ares":
                return fullscreen
                    ? ["--system", "Nintendo 64", "--no-file-prompt", "--fullscreen", rom]
                    : ["--system", "Nintendo 64", "--no-file-prompt", rom];
            case "cemu":
            case "eden":
            case "eglantine":
                return fullscreen ? ["-f", "-g", rom] : ["-g", rom];
            case "duckstation":
            case "pcsx2":
                return fullscreen ? ["-batch", "-fullscreen", "--", rom] : ["-batch", "--", rom];
            case "rpcs3":
                return fullscreen ? ["--no-gui", "--fullscreen", rom] : ["--no-gui", rom];
            case "xenia":
                return fullscreen ? [rom, "--fullscreen=true"] : [rom];
            case "snes9x":
            case "project64":
                return [rom];
            case "flycast":
                return fullscreen ? ["-config", "window:fullscreen=yes", rom] : [rom];
            case "mame":
            {
                var directory = Path.GetDirectoryName(rom) ?? ".";
                var setName = Path.GetFileNameWithoutExtension(rom);
                return fullscreen
                    ? ["-nowindow", "-rompath", directory, setName]
                    : ["-window", "-rompath", directory, setName];
            }
            default:
                throw new InvalidOperationException($"Unsupported emulator '{emulatorId}'.");
        }
    }

    /// <summary>
    /// Project64 and Snes9x read fullscreen from their own configuration
    /// instead of a reliable command-line flag. Failures are ignored: both lock
    /// their configuration while running and must still start.
    /// </summary>
    public static void PrepareFullscreenConfiguration(
        string emulatorId,
        string executablePath,
        bool fullscreen)
    {
        try
        {
            var directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            if (emulatorId.Equals("project64", StringComparison.OrdinalIgnoreCase))
            {
                var configPath = Path.Combine(directory, "Config", "Project64.cfg");
                UpdateTextFile(configPath, content => UpsertIniSetting(
                    content,
                    "Settings",
                    "Auto Full Screen",
                    fullscreen ? "1" : "0"));
            }
            else if (emulatorId.Equals("snes9x", StringComparison.OrdinalIgnoreCase))
            {
                var value = fullscreen ? "TRUE" : "FALSE";
                UpdateTextFile(Path.Combine(directory, "snes9x.conf"), content =>
                    UpsertFlatSetting(
                        UpsertFlatSetting(content, "Fullscreen:FullscreenOnOpen", value),
                        "Fullscreen:EmulateFullscreen",
                        value));
            }
        }
        catch
        {
        }
    }

    /// <summary>Marker files that keep a TFS-installed emulator's data next to it.</summary>
    public static void ConfigurePortableMode(string emulatorId, string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        switch (emulatorId.ToLowerInvariant())
        {
            case "duckstation":
            case "dolphin":
                var marker = Path.Combine(directory, "portable.txt");
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, string.Empty);
                }
                break;
            case "cemu":
                Directory.CreateDirectory(Path.Combine(directory, "portable"));
                break;
            case "eden":
            case "eglantine":
            case "azahar":
                Directory.CreateDirectory(Path.Combine(directory, "user"));
                break;
        }
    }

    internal static string UpsertIniSetting(string content, string section, string key, string value)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count == 1 && lines[0].Length == 0)
        {
            lines.Clear();
        }

        var sectionIndex = lines.FindIndex(line =>
            line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return string.Join(newline, lines);
        }

        var sectionEnd = lines.Count;
        for (var index = sectionIndex + 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                sectionEnd = index;
                break;
            }
        }

        for (var index = sectionIndex + 1; index < sectionEnd; index++)
        {
            if (IsSettingLine(lines[index], key))
            {
                lines[index] = $"{key}={value}";
                return string.Join(newline, lines);
            }
        }

        lines.Insert(sectionIndex + 1, $"{key}={value}");
        return string.Join(newline, lines);
    }

    internal static string UpsertFlatSetting(string content, string key, string value)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count == 1 && lines[0].Length == 0)
        {
            lines.Clear();
        }

        var index = lines.FindIndex(line => IsSettingLine(line, key));
        if (index >= 0)
        {
            lines[index] = $"{key} = {value}";
        }
        else
        {
            lines.Add($"{key} = {value}");
        }

        return string.Join(newline, lines);
    }

    private static bool IsSettingLine(string line, string key)
    {
        var separator = line.IndexOf('=');
        return separator > 0 &&
               line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateTextFile(string path, Func<string, string> update)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var updated = update(content);
        if (updated.Equals(content, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
    }
}
