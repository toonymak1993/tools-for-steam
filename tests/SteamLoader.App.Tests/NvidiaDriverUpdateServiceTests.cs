using System.Diagnostics;
using System.Xml.Linq;
using SteamLoader.App.Infrastructure.SystemTools;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class NvidiaDriverUpdateServiceTests
{
    [Fact]
    public void PinnedRelease_UsesOfficialVersionedAssetAndSha256()
    {
        Assert.Equal("1.25.2", NvidiaDriverUpdateService.Version);
        Assert.Equal(
            "https://github.com/HawaiiBeach/TinyNvidiaUpdateChecker/releases/download/v1.25.2/TinyNvidiaUpdateChecker.exe",
            NvidiaDriverUpdateService.DownloadUrl);
        Assert.Matches("^[0-9a-f]{64}$", NvidiaDriverUpdateService.Sha256);
    }

    [Fact]
    public void SilentGameReadyStartInfo_UsesUnattendedUpdateArgumentsAndElevation()
    {
        const string toolPath = @"C:\Tools for Steam\TinyNvidiaUpdateChecker.exe";
        const string configPath =
            @"C:\Tools for Steam\data\system\driver\silent-game-ready.config";

        var startInfo = NvidiaDriverUpdateService.CreateSilentGameReadyStartInfo(
            toolPath,
            configPath);

        Assert.Equal(toolPath, startInfo.FileName);
        Assert.Equal(@"C:\Tools for Steam", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            [
                "--quiet",
                "--noprompt",
                "--confirm-dl",
                $"--config-override={configPath}"
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain("--force-dl", startInfo.ArgumentList);
    }

    [Fact]
    public void SilentGameReadyConfig_SelectsGameReadyAndDisablesMinimalInstall()
    {
        var document = XDocument.Parse(
            NvidiaDriverUpdateService.CreateSilentGameReadyConfig());
        var settings = document
            .Descendants("add")
            .ToDictionary(
                element => element.Attribute("key")!.Value,
                element => element.Attribute("value")!.Value);

        Assert.Equal("false", settings["Check for Updates"]);
        Assert.Equal("false", settings["Minimal install"]);
        Assert.Equal("grd", settings["Driver type"]);
    }

    [Fact]
    public void LaunchStartInfo_IsVisibleAndKeepsConfigPathInOneArgument()
    {
        const string toolPath = @"C:\Tools for Steam\TinyNvidiaUpdateChecker.exe";
        const string configPath = @"C:\Tools for Steam\data\system\driver\app.config";

        var startInfo = NvidiaDriverUpdateService.CreateLaunchStartInfo(toolPath, configPath);

        Assert.Equal(toolPath, startInfo.FileName);
        Assert.Equal(@"C:\Tools for Steam", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(
            [$"--config-override={configPath}"],
            startInfo.ArgumentList);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", "NVIDIA", null, true)]
    [InlineData("Microsoft Basic Display Adapter", null, @"PCI\VEN_10DE&DEV_2C02", true)]
    [InlineData("AMD Radeon RX 9070 XT", "Advanced Micro Devices, Inc.", @"PCI\VEN_1002&DEV_7550", false)]
    [InlineData("Intel Arc B580", "Intel Corporation", @"PCI\VEN_8086&DEV_E20B", false)]
    public void IsNvidiaAdapter_RecognizesNvidiaNamesAndPciVendorIds(
        string? name,
        string? adapterCompatibility,
        string? pnpDeviceId,
        bool expected)
    {
        Assert.Equal(
            expected,
            NvidiaDriverUpdateService.IsNvidiaAdapter(
                name,
                adapterCompatibility,
                pnpDeviceId));
    }
}
