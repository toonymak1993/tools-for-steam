using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryEmulationTests
{
    [Theory]
    [InlineData("game-boy-color", "Metroid II.gb", "game-boy")]
    [InlineData("game-boy-color", "Link's Awakening DX.gbc", "game-boy-color")]
    [InlineData("game-boy-color", "Yu-Gi-Oh.gba", "game-boy-advance")]
    [InlineData("wii-u", "Wind Waker.rvz", "gamecube")]
    [InlineData("wii", "Mario Kart.rvz", "wii")]
    [InlineData("switch-2", "Zelda.xci", "switch-2")]
    [InlineData("switch", "Metroid Dread.xci", "switch")]
    [InlineData("psp", "Daxter.iso", "psp")]
    [InlineData("snes", "readme.txt", null)]
    [InlineData("mega-drive", "Sonic.bin", null)]
    [InlineData("arcade", "neogeo.zip", null)]
    [InlineData("arcade", "mslug.zip", "arcade")]
    public void DetectSystem_KeepsFolderExtensionsAndRoutesUniqueExtensions(
        string folderSystemId,
        string fileName,
        string? expectedSystemId)
    {
        var folder = OmniLibraryRomSystemRegistry.GetRequired(folderSystemId);
        var detected = OmniLibraryRomSystemRegistry.DetectSystem(
            folder,
            Path.Combine(Path.GetTempPath(), "Roms", folder.FolderName, fileName));

        Assert.Equal(expectedSystemId, detected?.Id);
    }

    [Fact]
    public void DetectSystem_AcceptsPs3AndXbox360BootFilesOnlyInTheirLayout()
    {
        var ps3 = OmniLibraryRomSystemRegistry.GetRequired("playstation-3");
        var xbox360 = OmniLibraryRomSystemRegistry.GetRequired("xbox-360");
        var root = Path.Combine(Path.GetTempPath(), "Roms");

        Assert.Equal(
            "playstation-3",
            OmniLibraryRomSystemRegistry.DetectSystem(
                ps3,
                Path.Combine(root, "PlayStation 3", "Demon's Souls", "PS3_GAME", "USRDIR", "EBOOT.BIN"))?.Id);
        Assert.Null(OmniLibraryRomSystemRegistry.DetectSystem(
            ps3,
            Path.Combine(root, "PlayStation 3", "Tools", "EBOOT.BIN")));
        Assert.Equal(
            "xbox-360",
            OmniLibraryRomSystemRegistry.DetectSystem(
                xbox360,
                Path.Combine(root, "Xbox 360", "Halo 3", "default.xex"))?.Id);
    }

    [Theory]
    [InlineData("Pokemon - FireRed Version (USA, Europe) (Rev 1).gba", "Pokemon - FireRed Version")]
    [InlineData("Legend of Zelda, The - Link's Awakening (USA, Europe) (Rev 2).gb", "The Legend of Zelda - Link's Awakening")]
    [InlineData("Dragon Ball Z - Gokuu Gekitouden (J) [S].gb", "Dragon Ball Z - Gokuu Gekitouden")]
    [InlineData("1435 - Yu-Gi-Oh! - World Championship Tournament 2004 (E)(GBA).gba", "Yu-Gi-Oh! - World Championship Tournament 2004")]
    [InlineData("Grand Theft Auto - Chinatown Wars (Europe) (PSP) (PSN).iso", "Grand Theft Auto - Chinatown Wars")]
    [InlineData("Crash of the Titans (Europe) (De,Ru).iso", "Crash of the Titans")]
    [InlineData("The Legend of Zelda Ocarina of Time [0400486024dec000][v131072].nup", "The Legend of Zelda Ocarina of Time")]
    [InlineData("Final Fantasy (II).sfc", "Final Fantasy (II)")]
    public void BuildDisplayTitle_RemovesCatalogTagsLikeOrbit(string fileName, string expected)
    {
        Assert.Equal(expected, OmniLibraryRomSystemRegistry.BuildDisplayTitle(fileName));
    }

    [Fact]
    public void BuildDisplayTitle_NamesGenericBootFilesAfterTheirGameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Roms");
        Assert.Equal(
            "Demon's Souls",
            OmniLibraryRomSystemRegistry.BuildDisplayTitle(
                Path.Combine(root, "PlayStation 3", "Demon's Souls", "PS3_GAME", "USRDIR", "EBOOT.BIN")));
        Assert.Equal(
            "Halo 3",
            OmniLibraryRomSystemRegistry.BuildDisplayTitle(Path.Combine(root, "Xbox 360", "Halo 3", "default.xex")));
        Assert.Equal(
            "Mario Kart 8",
            OmniLibraryRomSystemRegistry.BuildDisplayTitle(
                Path.Combine(root, "Wii U", "Mario Kart 8", "code", "Turbo.rpx")));
    }

    [Fact]
    public void RomLibrary_ImportsPlaylistsOnceAndRoutesMisplacedCartridges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-rom-emulation-test-{Guid.NewGuid():N}");
        try
        {
            var store = new UnifySteamStoreConfiguration { Enabled = true, InstallPath = root, ImportOrbitRoms = false, ImportEmulatorLibraries = false };
            OmniLibraryRomSystemRegistry.EnsureFolderStructure(root);
            var ps1 = Path.Combine(root, "PlayStation", "Final Fantasy VII");
            Directory.CreateDirectory(ps1);
            Write(Path.Combine(ps1, "Final Fantasy VII (Disc 1).chd"));
            Write(Path.Combine(ps1, "Final Fantasy VII (Disc 2).chd"));
            Write(Path.Combine(ps1, "Final Fantasy VII.m3u"), "Final Fantasy VII (Disc 1).chd\nFinal Fantasy VII (Disc 2).chd\n");
            Write(Path.Combine(root, "Game Boy Color", "Tetris.gb"));

            OmniLibraryRomLibrary.Refresh(store);

            Assert.Equal(2, store.Cache.Games.Count);
            var playlist = Assert.Single(store.Cache.Games, game => game.PlatformId == "playstation");
            Assert.EndsWith(".m3u", playlist.RomPath, StringComparison.OrdinalIgnoreCase);
            var tetris = Assert.Single(store.Cache.Games, game => game.PlatformId == "game-boy");
            Assert.Equal("Game Boy", tetris.PlatformTitle);
            Assert.Contains("Nintendo%20-%20Game%20Boy/", tetris.ImageUrl);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Resolve_PrefersAnExplicitWorkingExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-emulator-resolve-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "mGBA.exe");
            File.WriteAllBytes(executable, [0x4D, 0x5A]);
            var system = OmniLibraryRomSystemRegistry.GetRequired("game-boy-advance");

            var selection = OmniLibraryEmulatorDetector.Resolve(system, null, executable);

            Assert.True(selection.Ready);
            Assert.Equal("mgba", selection.Emulator?.Id);
            Assert.Equal(executable, selection.ExecutablePath);

            // An executable that belongs to no emulator of this system is ignored.
            var foreign = Path.Combine(root, "Dolphin.exe");
            File.WriteAllBytes(foreign, [0x4D, 0x5A]);
            var other = OmniLibraryEmulatorDetector.Resolve(system, "mgba", foreign);
            Assert.NotEqual(foreign, other.ExecutablePath);
            Assert.Equal("mgba", other.InstallCandidate?.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GithubAssetSelection_RequiresExactlyOneWindowsPackageAndReadsItsDigest()
    {
        var ppsspp = OmniLibraryEmulatorCatalog.GetRequired("ppsspp").Package;
        var digest = new string('a', 64);
        using var release = JsonDocument.Parse($$"""
            {"assets":[
              {"name":"PPSSPP-v1.20.4-Windows-x64.zip","size":1234,"digest":"sha256:{{digest}}","browser_download_url":"https://github.com/hrydgard/ppsspp/releases/download/v1.20.4/PPSSPP-v1.20.4-Windows-x64.zip"},
              {"name":"PPSSPP-v1.20.4-Android.apk","size":99,"browser_download_url":"https://github.com/x.apk"}
            ]}
            """);

        var asset = OmniLibraryEmulatorInstaller.SelectGithubAssetInfo(ppsspp, release.RootElement);

        Assert.Equal("PPSSPP-v1.20.4-Windows-x64.zip", asset.FileName);
        Assert.Equal(1234, asset.ExpectedBytes);
        Assert.Equal(digest, asset.Sha256);

        using var untrusted = JsonDocument.Parse("""
            {"assets":[{"name":"PPSSPP-v1.20.4-Windows-x64.zip","browser_download_url":"https://example.com/p.zip"}]}
            """);
        Assert.Throws<InvalidOperationException>(() =>
            OmniLibraryEmulatorInstaller.SelectGithubAssetInfo(ppsspp, untrusted.RootElement));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("dir/../../evil.exe")]
    public void ArchiveEntries_RejectEscapingPaths(string entry)
    {
        Assert.Throws<InvalidDataException>(() => OmniLibraryEmulatorInstaller.ValidateEntryPath(entry));
    }

    [Fact]
    public void FullscreenConfiguration_UpdatesOnlyTheTargetSetting()
    {
        var ini = "[Settings]\r\nOther=1\r\nAuto Full Screen=0\r\n[Next]\r\nAuto Full Screen=0";
        var updated = OmniLibraryEmulatorCatalog.UpsertIniSetting(ini, "Settings", "Auto Full Screen", "1");
        Assert.Equal("[Settings]\r\nOther=1\r\nAuto Full Screen=1\r\n[Next]\r\nAuto Full Screen=0", updated);

        var created = OmniLibraryEmulatorCatalog.UpsertIniSetting(string.Empty, "Settings", "Auto Full Screen", "1");
        Assert.Equal("[Settings]\nAuto Full Screen=1", created);

        var flat = OmniLibraryEmulatorCatalog.UpsertFlatSetting(
            "Fullscreen:FullscreenOnOpen = FALSE\nX = 1",
            "Fullscreen:FullscreenOnOpen",
            "TRUE");
        Assert.Equal("Fullscreen:FullscreenOnOpen = TRUE\nX = 1", flat);
    }

    [Fact]
    public void RetroArchLaunch_NeedsACore()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OmniLibraryEmulatorCatalog.BuildLaunchArguments("retroarch", "C:\\Roms\\NES\\game.nes", null, true));
        var arguments = OmniLibraryEmulatorCatalog.BuildLaunchArguments(
            "retroarch",
            "C:\\Roms\\NES\\game.nes",
            "C:\\RA\\cores\\mesen_libretro.dll",
            true);
        Assert.Equal(["-f", "-L", "C:\\RA\\cores\\mesen_libretro.dll", "C:\\Roms\\NES\\game.nes"], arguments);
    }

    [Theory]
    [InlineData("GZLE01", "gamecube")]
    [InlineData("RMCP01", "wii")]
    [InlineData("SB4E01", "wii")]
    public void DolphinGameFolders_SplitGameCubeAndWiiByDiscId(string discId, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-dolphin-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var iso = Path.Combine(root, "game.iso");
            var bytes = new byte[64];
            System.Text.Encoding.ASCII.GetBytes(discId).CopyTo(bytes, 0);
            File.WriteAllBytes(iso, bytes);

            var rvz = Path.Combine(root, "game.rvz");
            var rvzBytes = new byte[0x80];
            System.Text.Encoding.ASCII.GetBytes("RVZ\u0001").CopyTo(rvzBytes, 0);
            System.Text.Encoding.ASCII.GetBytes(discId).CopyTo(rvzBytes, 0x58);
            File.WriteAllBytes(rvz, rvzBytes);

            var systems = new[]
            {
                OmniLibraryRomSystemRegistry.GetRequired("gamecube"),
                OmniLibraryRomSystemRegistry.GetRequired("wii"),
            };
            Assert.Equal(expected, OmniLibraryEmulatorGameLists.DetectForSource(systems, iso)?.Id);
            Assert.Equal(expected, OmniLibraryEmulatorGameLists.DetectForSource(systems, rvz)?.Id);
            Assert.Null(OmniLibraryEmulatorGameLists.DetectForSource(systems, Path.Combine(root, "notes.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmulatorIni_KeepsRepeatedGameListKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tfs-ini-test-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllText(path, "[GameList]\r\nRecursivePaths = D:/PS2\r\nRecursivePaths = E:/More PS2\r\nPaths = F:/Flat\r\n[Other]\r\nX=1");
            var section = OmniLibraryEmulatorGameLists.ReadIni(path)["GameList"];
            Assert.Equal(3, section.Count);
            Assert.Equal("E:/More PS2", section[1].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Write(string path, string content = "rom")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-5));
    }
}
