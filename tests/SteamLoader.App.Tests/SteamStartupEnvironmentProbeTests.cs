using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SteamStartupEnvironmentProbeTests
{
    [Fact]
    public void NormalizeSteamLibraryRoots_IncludesPrimaryAndValidSecondaryLibraries()
    {
        const string content = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\SteamLibrary" }
                "2" { "path" "d:\\steamlibrary\\" }
                "3" { "path" "relative\\unsafe" }
            }
            """;

        var roots = SteamStartupEnvironmentProbe.NormalizeSteamLibraryRoots(
            @"C:\Program Files (x86)\Steam",
            content);

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, path => path.Equals(
            @"C:\Program Files (x86)\Steam",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, path => path.Equals(
            @"D:\SteamLibrary",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeSteamLibraryRoots_IgnoresMalformedEntries()
    {
        const string content = """
            "libraryfolders"
            {
                "1" { "path" "" }
                "2" { "path" "::not-a-root::" }
                "3" { "path_without_value" }
            }
            """;

        var roots = SteamStartupEnvironmentProbe.NormalizeSteamLibraryRoots(
            @"C:\Steam",
            content);

        Assert.Single(roots);
        Assert.Equal(@"C:\Steam", roots[0], ignoreCase: true);
    }

    [Fact]
    public void IsBootstrapUpdateActive_CompletionAfterActivityIsNotActive()
    {
        const string log = "Checking for available updates...\nDownloading manifest...\nNothing to do";

        Assert.False(SteamStartupEnvironmentProbe.IsBootstrapUpdateActive(log));
    }

    [Fact]
    public void IsBootstrapUpdateActive_NewActivityAfterOldCompletionIsActive()
    {
        const string log = "Update complete\nLaunching Steam\nChecking for update on startup";

        Assert.True(SteamStartupEnvironmentProbe.IsBootstrapUpdateActive(log));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Steam started normally")]
    [InlineData("Checking for update\nAlready up to date")]
    public void IsBootstrapUpdateActive_RejectsInactiveLogs(string log)
    {
        Assert.False(SteamStartupEnvironmentProbe.IsBootstrapUpdateActive(log));
    }
}
