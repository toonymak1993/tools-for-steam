using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class UnifySteamGogTests
{
    [Fact]
    public void ResolveGogLaunchTask_PrefersRealGameOverPrimaryLauncher()
    {
        var installRoot = CreateInstallRoot();
        try
        {
            File.WriteAllText(Path.Combine(installRoot, "launcher.exe"), string.Empty);
            var systemDirectory = Directory.CreateDirectory(Path.Combine(installRoot, "System")).FullName;
            File.WriteAllText(Path.Combine(systemDirectory, "witcher.exe"), string.Empty);
            WriteManifest(
                installRoot,
                "1207658924",
                """
                [
                  {
                    "type": "FileTask",
                    "path": "launcher.exe",
                    "category": "launcher",
                    "isPrimary": true
                  },
                  {
                    "type": "FileTask",
                    "path": "System\\witcher.exe",
                    "workingDir": "System",
                    "arguments": "-windowed",
                    "category": "game",
                    "isHidden": true,
                    "compatibilityFlags": "DISABLEDWM HIGHDPIAWARE RUNASADMIN WIN7RTM"
                  },
                  {
                    "type": "FileTask",
                    "path": "System\\witcher.exe",
                    "workingDir": "System",
                    "arguments": "-dontForceMinReqs",
                    "category": "game"
                  }
                ]
                """);

            var task = UnifySteamLauncher.ResolveGogLaunchTask(installRoot, "1207658924");

            Assert.NotNull(task);
            Assert.Equal(1, task.Index);
            Assert.Equal(Path.Combine(systemDirectory, "witcher.exe"), task.ExecutablePath);
            Assert.Equal(systemDirectory, task.WorkingDirectory);
            Assert.Equal("-windowed", task.RawArguments);
            Assert.Equal("game", task.Category);
            Assert.False(task.IsPrimary);
            Assert.True(task.RequiresElevation);
            Assert.Contains("RUNASADMIN", task.CompatibilityFlags);
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveGogSetupPlan_UsesManifestSetupAndDependencies()
    {
        var installRoot = CreateInstallRoot();
        var runtimeRoot = CreateInstallRoot();
        try
        {
            var manifestDirectory = Directory.CreateDirectory(
                Path.Combine(runtimeRoot, "heroic_gogdl", "manifests")).FullName;
            File.WriteAllText(
                Path.Combine(manifestDirectory, "1207658924"),
                """
                {
                  "version": 2,
                  "buildId": "58076094395251196",
                  "HGLInstallLanguage": "en-US",
                  "dependencies": ["MSVC2005", "MSVC2005_x64", "DirectX"],
                  "products": [
                    {
                      "productId": "1207658924",
                      "temp_executable": "galaxy_the_witcher_enhanced_edition.exe"
                    },
                    {
                      "productId": "unrelated-dlc",
                      "temp_executable": "ignored.exe"
                    }
                  ]
                }
                """);

            var plan = GogInstallPreparation.ResolvePlan(
                runtimeRoot,
                installRoot,
                "1207658924");

            Assert.NotNull(plan);
            Assert.Equal("58076094395251196", plan.BuildId);
            Assert.Equal("en-US", plan.LanguageCode);
            Assert.Equal("english", plan.LanguageName);
            Assert.Equal(
                ["DirectX", "MSVC2005", "MSVC2005_x64"],
                plan.Dependencies);
            var command = Assert.Single(plan.Commands);
            Assert.Equal("1207658924", command.ProductId);
            Assert.Equal(
                "galaxy_the_witcher_enhanced_edition.exe",
                command.ExecutableName);
            Assert.False(command.UsesScriptInterpreter);
            Assert.Matches("^[A-F0-9]{64}$", plan.Signature);
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveGogSetupPlan_RejectsUnsafeSetupPath()
    {
        var installRoot = CreateInstallRoot();
        var runtimeRoot = CreateInstallRoot();
        try
        {
            var manifestDirectory = Directory.CreateDirectory(
                Path.Combine(runtimeRoot, "heroic_gogdl", "manifests")).FullName;
            File.WriteAllText(
                Path.Combine(manifestDirectory, "42"),
                """
                {
                  "version": 2,
                  "buildId": "build",
                  "products": [
                    {
                      "productId": "42",
                      "temp_executable": "..\\outside.exe"
                    }
                  ]
                }
                """);

            var plan = GogInstallPreparation.ResolvePlan(
                runtimeRoot,
                installRoot,
                "42");

            Assert.Null(plan);
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void ClearGogInstalledState_RemovesOnlyPerGameManifestAndSupport()
    {
        var gameId = $"test-{Guid.NewGuid():N}";
        var manifestPath = ManagedGogDlHelper.GetInstalledManifestPath(gameId);
        var supportDirectory = ManagedGogDlHelper.GetSupportDirectory(gameId);
        var unrelatedPath = Path.Combine(
            ManagedGogDlHelper.ConfigDirectory,
            $"unrelated-{Guid.NewGuid():N}.marker");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            Directory.CreateDirectory(supportDirectory);
            File.WriteAllText(manifestPath, "{}");
            File.WriteAllText(Path.Combine(supportDirectory, "setup.exe"), "test");
            Directory.CreateDirectory(ManagedGogDlHelper.ConfigDirectory);
            File.WriteAllText(unrelatedPath, "keep");

            ManagedGogDlHelper.ClearInstalledState(gameId);

            Assert.False(File.Exists(manifestPath));
            Assert.False(Directory.Exists(supportDirectory));
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }

            if (Directory.Exists(supportDirectory))
            {
                Directory.Delete(supportDirectory, recursive: true);
            }

            if (File.Exists(unrelatedPath))
            {
                File.Delete(unrelatedPath);
            }
        }
    }

    [Fact]
    public void GogInstalledStatePaths_RejectTraversal()
    {
        Assert.Throws<InvalidOperationException>(
            () => ManagedGogDlHelper.GetInstalledManifestPath(@"..\outside"));
        Assert.Throws<InvalidOperationException>(
            () => ManagedGogDlHelper.GetSupportDirectory(@"folder\outside"));
    }

    [Fact]
    public void ResolveGogLaunchTask_RejectsManifestPathOutsideInstallRoot()
    {
        var installRoot = CreateInstallRoot();
        var outsideDirectory = CreateInstallRoot();
        try
        {
            File.WriteAllText(Path.Combine(outsideDirectory, "outside.exe"), string.Empty);
            File.WriteAllText(Path.Combine(installRoot, "safe.exe"), string.Empty);
            WriteManifest(
                installRoot,
                "42",
                $$"""
                [
                  {
                    "type": "FileTask",
                    "path": "{{JsonEncoded(Path.Combine(outsideDirectory, "outside.exe"))}}",
                    "category": "game",
                    "isPrimary": true
                  },
                  {
                    "type": "FileTask",
                    "path": "safe.exe",
                    "category": "game"
                  }
                ]
                """);

            var task = UnifySteamLauncher.ResolveGogLaunchTask(installRoot, "42");

            Assert.NotNull(task);
            Assert.Equal(1, task.Index);
            Assert.Equal(Path.Combine(installRoot, "safe.exe"), task.ExecutablePath);
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void GogOperationJournal_PreservesResumePhaseAfterFailure()
    {
        var gameId = $"test-{Guid.NewGuid():N}";
        var installRoot = CreateInstallRoot();
        try
        {
            GogOperationJournal.BeginInstall(
                gameId,
                installRoot,
                includeDlc: true,
                operation: "repair");
            GogOperationJournal.Advance(
                gameId,
                GogOperationPhases.Downloading,
                downloadedBytes: 2048,
                totalBytes: 8192,
                attempt: 2);

            GogOperationJournal.Fail(gameId, "connection interrupted");

            var transaction = GogOperationJournal.Get(gameId);
            Assert.NotNull(transaction);
            Assert.Equal(GogOperationPhases.Failed, transaction.Phase);
            Assert.Equal(GogOperationPhases.Downloading, transaction.ResumePhase);
            Assert.Equal(2048, transaction.DownloadedBytes);
            Assert.Equal(8192, transaction.TotalBytes);
            Assert.Equal(2, transaction.Attempt);
            Assert.True(transaction.IncludeDlc);
            Assert.True(transaction.IsRecoverableInstall);
            Assert.True(transaction.IsRepair);
        }
        finally
        {
            GogOperationJournal.Clear(gameId);
            Directory.Delete(installRoot, recursive: true);
        }
    }

    [Fact]
    public void GogInstallStateTracker_UsesExactProductManifestAndExecutable()
    {
        var gameId = $"test-{Guid.NewGuid():N}";
        var configuredRoot = CreateInstallRoot();
        var installRoot = Directory.CreateDirectory(
            Path.Combine(configuredRoot, gameId)).FullName;
        try
        {
            File.WriteAllText(Path.Combine(installRoot, "game.exe"), string.Empty);
            File.WriteAllText(
                Path.Combine(installRoot, $"goggame-{gameId}.info"),
                """
                {
                  "buildId": "build-42",
                  "playTasks": [
                    {
                      "type": "FileTask",
                      "path": "game.exe",
                      "category": "game",
                      "isPrimary": true
                    }
                  ]
                }
                """);

            var probe = GogInstallStateTracker.Probe(
                gameId,
                configuredRoot,
                cachedInstallPath: null,
                force: true);

            Assert.True(probe.Conclusive);
            Assert.True(probe.Installed);
            Assert.Equal(installRoot, probe.InstallPath);
            Assert.Equal(Path.Combine(installRoot, "game.exe"), probe.ExecutablePath);
            Assert.Equal("build-42", probe.BuildId);
        }
        finally
        {
            GogOperationJournal.Clear(gameId);
            Directory.Delete(configuredRoot, recursive: true);
        }
    }

    [Fact]
    public void GogInstallStateTracker_DoesNotTreatPartialTransactionAsUninstalled()
    {
        var gameId = $"test-{Guid.NewGuid():N}";
        var configuredRoot = CreateInstallRoot();
        var installRoot = Directory.CreateDirectory(
            Path.Combine(configuredRoot, gameId)).FullName;
        try
        {
            GogOperationJournal.BeginInstall(
                gameId,
                installRoot,
                includeDlc: false);
            GogOperationJournal.Advance(
                gameId,
                GogOperationPhases.Downloading,
                downloadedBytes: 1024,
                totalBytes: 8192);

            var probe = GogInstallStateTracker.Probe(
                gameId,
                configuredRoot,
                installRoot,
                force: true);

            Assert.False(probe.Conclusive);
            Assert.False(probe.Installed);
        }
        finally
        {
            GogOperationJournal.Clear(gameId);
            Directory.Delete(configuredRoot, recursive: true);
        }
    }

    private static string CreateInstallRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tfs-gog-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteManifest(string installRoot, string gameId, string playTasks)
    {
        File.WriteAllText(
            Path.Combine(installRoot, $"goggame-{gameId}.info"),
            $$"""
            {
              "playTasks": {{playTasks}}
            }
            """);
    }

    private static string JsonEncoded(string value)
    {
        return JsonSerializer.Serialize(value)[1..^1];
    }
}
