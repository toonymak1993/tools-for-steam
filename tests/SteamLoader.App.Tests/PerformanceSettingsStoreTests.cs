using SteamLoader.App.Infrastructure.Performance;
using Xunit;
using System.Diagnostics;

namespace SteamLoader.App.Tests;

public sealed class PerformanceSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tfs-performance-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_NormalizesRtssModeAndFrameLimit()
    {
        var store = new PerformanceSettingsStore(Path.Combine(_directory, "performance.json"));
        store.Save(new PerformanceSettingsConfiguration
        {
            OverlayLevel = 99,
            FrameLimit = 999
        });

        var settings = store.Load();

        Assert.Equal(4, settings.OverlayLevel);
        Assert.True(settings.OverlayEnabled);
        Assert.Equal(360, settings.FrameLimit);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    public void Load_MigratesLegacyOverlayLevelsToRtssModes(int legacyLevel, int expectedRtssMode)
    {
        var path = Path.Combine(_directory, $"legacy-{legacyLevel}.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, $$"""{ "overlayEnabled": true, "overlayLevel": {{legacyLevel}} }""");

        var settings = new PerformanceSettingsStore(path).Load();

        Assert.Equal(expectedRtssMode, settings.OverlayLevel);
        Assert.True(settings.OverlayEnabled);
        Assert.Equal(3, settings.RtssSchemaVersion);
    }

    [Fact]
    public void Load_MigratesDisabledRtssSliderToOff()
    {
        var path = Path.Combine(_directory, "rtss-schema-1-disabled.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{ "rtssSchemaVersion": 1, "overlayEnabled": false, "overlayLevel": 3 }""");

        var settings = new PerformanceSettingsStore(path).Load();

        Assert.Equal(0, settings.OverlayLevel);
        Assert.False(settings.OverlayEnabled);
        Assert.Equal(3, settings.RtssSchemaVersion);
    }

    [Fact]
    public void Load_MigratesEnabledRtssSliderAndReservesZeroForOff()
    {
        var path = Path.Combine(_directory, "rtss-schema-1-enabled.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{ "rtssSchemaVersion": 1, "overlayEnabled": true, "overlayLevel": 0 }""");

        var settings = new PerformanceSettingsStore(path).Load();

        Assert.Equal(1, settings.OverlayLevel);
        Assert.True(settings.OverlayEnabled);
        Assert.Equal(3, settings.RtssSchemaVersion);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 20)]
    [InlineData(30, 30)]
    public void Save_NormalizesRtssFrameLimitRange(int requested, int expected)
    {
        var store = new PerformanceSettingsStore(Path.Combine(_directory, $"performance-{requested}.json"));
        store.Save(new PerformanceSettingsConfiguration { FrameLimit = requested });

        Assert.Equal(expected, store.Load().FrameLimit);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(80, 100)]
    [InlineData(100, 100)]
    [InlineData(140, 150)]
    [InlineData(999, 200)]
    public void Save_NormalizesOverlayScaleToNativeRtssZoom(int requested, int expected)
    {
        var store = new PerformanceSettingsStore(Path.Combine(_directory, $"scale-{requested}.json"));
        store.Save(new PerformanceSettingsConfiguration { OverlayScale = requested });

        Assert.Equal(expected, store.Load().OverlayScale);
    }

    [Theory]
    [InlineData(0, "<P0><L0>")]
    [InlineData(1, "<P2><L1>")]
    [InlineData(2, "<P6><L2>")]
    [InlineData(3, "<P8><L3>")]
    public void OverlayFormatter_UsesRtssStickyCornerTags(int position, string expectedPrefix)
    {
        var text = RtssOverlayFormatter.Wrap("FPS", position, 100, largeFont: false);

        Assert.StartsWith(expectedPrefix, text);
        Assert.Contains("<FNT=Unispace,-9,400,2>", text);
        Assert.EndsWith("FPS", text);
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    [InlineData(150, 3)]
    [InlineData(200, 4)]
    public void OverlayFormatter_MapsPercentToRtssZoomRatio(int scale, int expectedZoom)
    {
        var text = RtssOverlayFormatter.Wrap("FPS", 0, scale, largeFont: true);

        Assert.Contains($"<FNT=Unispace,-18,700,{expectedZoom}>", text);
    }

    [Fact]
    public void RtssProfileApi_SavesAndVerifiesPerGameFrameLimit()
    {
        var installedRtssPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "RivaTuner Statistics Server");
        var hooksName = Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll";
        var installedHooksPath = Path.Combine(installedRtssPath, hooksName);
        if (!File.Exists(installedHooksPath))
        {
            return;
        }

        var isolatedRtssPath = Path.Combine(_directory, "isolated-rtss");
        Directory.CreateDirectory(Path.Combine(isolatedRtssPath, "Profiles"));
        File.Copy(installedHooksPath, Path.Combine(isolatedRtssPath, hooksName));

        const string profileName = "ToolsForSteam.ProfileWriteTest.exe";
        using var profile = new RtssProfileClient(isolatedRtssPath);
        profile.ApplyGameProfile(profileName, 72);

        var profileText = File.ReadAllText(Path.Combine(isolatedRtssPath, "Profiles", profileName + ".cfg"));
        Assert.Contains("[Framerate]", profileText);
        Assert.Contains("Limit=72", profileText);
    }

    [Fact]
    public void InstalledRtss_ExposesSharedMemoryAndProfileApi()
    {
        var installPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "RivaTuner Statistics Server");
        if (!File.Exists(Path.Combine(installPath, "RTSSHooks64.dll")))
        {
            return;
        }

        var processes = Process.GetProcessesByName("RTSS");
        try
        {
            if (!processes.Any(process => !process.HasExited))
            {
                return;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        const string smokeTestProfile = "ToolsForSteam.RtssSmokeTest.exe";
        var installation = new RtssInstallationService().Detect();
        Assert.True(installation.Installed);
        Assert.True(installation.Running);
        var profilesPath = Path.Combine(installPath, "Profiles");
        var probePath = Path.Combine(profilesPath, $".tfs-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "probe");
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch
            {
            }
        }

        using var profile = new RtssProfileClient(installPath);
        var memory = new RtssSharedMemoryClient();
        try
        {
            profile.ApplyGameProfile(smokeTestProfile, 60);
            Assert.True(memory.TryWriteOverlay("<C=66C0F4>Tools for Steam RTSS smoke test", false));
        }
        finally
        {
            memory.ReleaseOverlay();
            profile.DeleteGameProfile(smokeTestProfile);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
