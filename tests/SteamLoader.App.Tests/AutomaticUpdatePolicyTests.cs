using SteamLoader.App.Models;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class AutomaticUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldInstall_NewInstallableRelease()
    {
        Assert.True(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot(), lastAttempt: null, Now));
    }

    [Fact]
    public void ShouldInstall_NotWithoutUpdateOrPackageOrWhileInstalling()
    {
        Assert.False(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot() with { UpdateAvailable = false }, null, Now));
        Assert.False(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot() with { CanInstall = false }, null, Now));
        Assert.False(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot() with { InstallInProgress = true }, null, Now));
    }

    [Fact]
    public void ShouldInstall_WaitsADayAfterAFailedAttemptOfTheSameVersion()
    {
        var recentAttempt = new AutomaticUpdateAttempt("0.4.1", Now.AddHours(-2));
        var oldAttempt = new AutomaticUpdateAttempt("0.4.1", Now.AddHours(-25));
        var otherVersionAttempt = new AutomaticUpdateAttempt("0.4.0", Now.AddMinutes(-5));

        Assert.False(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot(), recentAttempt, Now));
        Assert.True(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot(), oldAttempt, Now));
        Assert.True(AutomaticUpdatePolicy.ShouldInstall(CreateSnapshot(), otherVersionAttempt, Now));
    }

    [Fact]
    public void AttemptMarker_RoundTripsAndToleratesMissingOrBrokenFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "data", AutomaticUpdatePolicy.AttemptFileName);
            Assert.Null(AutomaticUpdatePolicy.ReadAttempt(path));

            var attempt = new AutomaticUpdateAttempt("0.4.1", Now);
            AutomaticUpdatePolicy.WriteAttempt(path, attempt);
            Assert.Equal(attempt, AutomaticUpdatePolicy.ReadAttempt(path));

            File.WriteAllText(path, "{ not json");
            Assert.Null(AutomaticUpdatePolicy.ReadAttempt(path));
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
    public void IsInstalledBuild_RequiresSetupUninstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(AutomaticUpdatePolicy.IsInstalledBuild(root));

            File.WriteAllBytes(Path.Combine(root, "unins000.exe"), []);
            Assert.True(AutomaticUpdatePolicy.IsInstalledBuild(root));
            Assert.False(AutomaticUpdatePolicy.IsInstalledBuild(Path.Combine(root, "missing")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static UpdateCheckSnapshot CreateSnapshot()
    {
        return new UpdateCheckSnapshot(
            CurrentVersion: "0.4.0",
            LatestVersion: "0.4.1",
            UpdateAvailable: true,
            CanInstall: true,
            Message: "Version 0.4.1 is available.",
            ReleaseUrl: "https://example.invalid/release",
            AssetName: "ToolsForSteamSetup.exe",
            PublishedAtUtc: Now,
            ReleaseName: "0.4.1",
            CheckedAtUtc: Now);
    }
}
