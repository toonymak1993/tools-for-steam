using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ProviderInstallStateTrackerTests
{
    [Fact]
    public void IdleReconciliation_ProbesGamesNotPreviouslyMarkedInstalled()
    {
        Assert.True(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            "idle",
            includeIdle: true));
    }

    [Theory]
    [InlineData("action-required")]
    [InlineData("uninstall-action-required")]
    [InlineData("uninstall-failed")]
    [InlineData("failed")]
    public void ActiveNativeHandoff_IsProbedBetweenIdlePasses(string status)
    {
        Assert.True(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            status,
            includeIdle: false));
    }

    [Fact]
    public void UnrelatedIdleGame_WaitsForTheNextProviderWidePass()
    {
        Assert.False(ProviderInstallStateTracker.ShouldScheduleGameProbe(
            "idle",
            includeIdle: false));
    }

    [Fact]
    public void DeletedFolderOnPresentDrive_IsMissing_AbsentDriveIsUnknown()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.False(ProviderInstallStateTracker.IsMissingFromAvailableVolume(root));
            Assert.True(ProviderInstallStateTracker.IsMissingFromAvailableVolume(
                Path.Combine(root, "deleted-game")));
            Assert.False(ProviderInstallStateTracker.IsOnUnavailableVolume(root));
            Assert.False(ProviderInstallStateTracker.IsMissingFromAvailableVolume(string.Empty));

            var absentDrive = FindAbsentDriveRoot();
            if (absentDrive is not null)
            {
                var gameOnAbsentDrive = Path.Combine(absentDrive, "Games", "Some Game");
                Assert.True(ProviderInstallStateTracker.IsOnUnavailableVolume(gameOnAbsentDrive));
                Assert.False(ProviderInstallStateTracker.IsMissingFromAvailableVolume(gameOnAbsentDrive));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EpicReconciliation_SeesLegendaryAndLauncherInstalls_AndDropsDeletedFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            var legendaryGame = Directory.CreateDirectory(Path.Combine(root, "LegendaryGame")).FullName;
            var launcherGame = Directory.CreateDirectory(Path.Combine(root, "LauncherGame")).FullName;
            var deletedLegendary = Path.Combine(root, "DeletedLegendary");
            var deletedLauncher = Path.Combine(root, "DeletedLauncher");
            var legendaryJson = Path.Combine(root, "installed.json");
            var launcherDat = Path.Combine(root, "LauncherInstalled.dat");
            File.WriteAllText(
                legendaryJson,
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["Owl"] = new
                    {
                        app_name = "Owl",
                        title = "Owl Game",
                        install_path = legendaryGame,
                        executable = "owl.exe",
                        version = "1.0",
                        is_dlc = false,
                    },
                    ["Gone"] = new
                    {
                        app_name = "Gone",
                        title = "Gone Game",
                        install_path = deletedLegendary,
                        executable = "gone.exe",
                        version = "1.0",
                        is_dlc = false,
                    },
                }));
            File.WriteAllText(
                launcherDat,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    InstallationList = new object[]
                    {
                        new { AppName = "Fox", InstallLocation = launcherGame },
                        new { AppName = "Badger", InstallLocation = deletedLauncher },
                    },
                }));

            var installed = UnifySteamService.LoadEpicInstalledGamesForReconciliation(
                [],
                legendaryJson,
                launcherDat);

            Assert.True(installed["Owl"].Installed);
            Assert.Equal(legendaryGame, installed["Owl"].InstallPath);
            Assert.True(installed["Fox"].Installed);
            Assert.False(installed.ContainsKey("Gone"));
            Assert.False(installed.ContainsKey("Badger"));
            Assert.True(UnifySteamService.IsLegendaryRecordWithDeletedFolder(legendaryJson, "Gone"));
            Assert.False(UnifySteamService.IsLegendaryRecordWithDeletedFolder(legendaryJson, "Owl"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EpicReconciliation_UnreadableInstallList_FailsInsteadOfClearingGames()
    {
        var root = CreateTempDirectory();
        try
        {
            var launcherDat = Path.Combine(root, "LauncherInstalled.dat");
            File.WriteAllText(launcherDat, "{ \"InstallationList\": [ { \"AppName\": ");

            Assert.Throws<IOException>(() =>
                UnifySteamService.LoadEpicInstalledGamesForReconciliation(
                    [],
                    Path.Combine(root, "missing-installed.json"),
                    launcherDat));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingGame_IsConfirmedSoonInsteadOfAfterAFullIdleInterval()
    {
        Assert.Equal(
            OmniLibraryInstallStateCoordinator.MissingConfirmationInterval,
            OmniLibraryInstallStateCoordinator.NextIdleDelay(succeeded: true, confirmationPending: true));
        Assert.Equal(
            OmniLibraryInstallStateCoordinator.IdleCheckInterval,
            OmniLibraryInstallStateCoordinator.NextIdleDelay(succeeded: true, confirmationPending: false));
        Assert.True(
            OmniLibraryInstallStateCoordinator.MissingConfirmationInterval <
            OmniLibraryInstallStateCoordinator.IdleCheckInterval);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tfs-install-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? FindAbsentDriveRoot()
    {
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            var root = $"{letter}:\\";
            if (!Directory.Exists(root))
            {
                return root;
            }
        }

        return null;
    }
}
