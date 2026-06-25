using System.Reflection;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class StoreSyncEaDetectionTests
{
    [Fact]
    public void FindBestExecutable_IgnoresEaActivationUiStub()
    {
        var root = CreateTempRoot();
        try
        {
            var gameDirectory = Path.Combine(root, "Dead Space 2 (DE)");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "Core"));
            File.WriteAllBytes(Path.Combine(gameDirectory, "Core", "ActivationUI.exe"), []);
            File.WriteAllBytes(Path.Combine(gameDirectory, "DeadSpace2.exe"), []);

            var method = typeof(StoreSyncService).GetMethod(
                "FindBestExecutable",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var result = (string?)method!.Invoke(null, [gameDirectory]);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(gameDirectory, "DeadSpace2.exe")),
                result);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData(true, 0, 0, 0, 0, true)]
    [InlineData(true, 1, 0, 0, 0, true)]
    [InlineData(true, 0, 1, 0, 0, false)]
    [InlineData(false, 0, 0, 0, 0, false)]
    public void CanCleanupEaMissingTitles_HandlesEmptyEaLibrariesSafely(
        bool launcherDetected,
        int availableRootCount,
        int missingRootCount,
        int installReferenceCount,
        int detectedGameCount,
        bool expected)
    {
        var method = typeof(StoreSyncService).GetMethod(
            "CanCleanupEaMissingTitles",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var availableRoots = Enumerable.Range(0, availableRootCount)
            .Select(index => index == 0 ? @"C:\EA Games" : $@"C:\EA Games {index}")
            .ToArray();
        var missingRoots = Enumerable.Range(0, missingRootCount)
            .Select(index => $@"D:\Missing EA Games {index}")
            .ToArray();

        var result = (bool?)method!.Invoke(null, [launcherDetected, availableRoots, missingRoots, installReferenceCount, detectedGameCount]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CanCleanupEaMissingTitles_AllowsLauncherOnlyRoots()
    {
        var root = CreateTempRoot();
        try
        {
            var electronicArtsRoot = Path.Combine(root, "Electronic Arts");
            Directory.CreateDirectory(Path.Combine(electronicArtsRoot, "EA Desktop"));

            var method = typeof(StoreSyncService).GetMethod(
                "CanCleanupEaMissingTitles",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var result = (bool?)method!.Invoke(null, [true, new[] { electronicArtsRoot }, Array.Empty<string>(), 0, 0]);

            Assert.True(result);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
