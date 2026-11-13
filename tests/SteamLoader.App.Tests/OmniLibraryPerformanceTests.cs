using System.IO;
using System.Reflection;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryPerformanceTests
{
    [Fact]
    public void WatcherTriggeredStoreChecksAreThrottledPerInterval()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(StoreSyncAutomationService.ShouldRunOmniLibraryWatchCheck(default, now));
        Assert.False(StoreSyncAutomationService.ShouldRunOmniLibraryWatchCheck(
            now - TimeSpan.FromSeconds(30),
            now));
        Assert.True(StoreSyncAutomationService.ShouldRunOmniLibraryWatchCheck(
            now - StoreSyncAutomationService.OmniLibraryWatchCheckInterval,
            now));
    }

    [Fact]
    public void RegisteredAppxManifestIsParsedOncePerManifestVersion()
    {
        var method = typeof(UnifySteamService).GetMethod(
            "TryGetRegisteredXboxAppxGame",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var packageRoot = Path.Combine(
            Path.GetTempPath(),
            $"tfs-appx-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        var packageFullName = $"Contoso.TestGame_1.0.0.0_x64__{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(manifestPath, BuildManifest(xboxLive: true));
            var writeTimeUtc = File.GetLastWriteTimeUtc(manifestPath);

            Assert.True(Invoke(method!, packageFullName, packageRoot, out var first));
            Assert.Equal("Contoso.TestGame", first.Id);

            // Same manifest version: the cached answer is reused without
            // reading the file again, even though its content changed.
            File.WriteAllText(manifestPath, BuildManifest(xboxLive: false));
            File.SetLastWriteTimeUtc(manifestPath, writeTimeUtc);
            Assert.True(Invoke(method!, packageFullName, packageRoot, out var cached));
            Assert.Equal(first.Id, cached.Id);
            Assert.NotSame(first, cached);

            // A new manifest version is parsed again.
            File.SetLastWriteTimeUtc(manifestPath, writeTimeUtc.AddMinutes(1));
            Assert.False(Invoke(method!, packageFullName, packageRoot, out _));
        }
        finally
        {
            Directory.Delete(packageRoot, recursive: true);
        }
    }

    private static bool Invoke(
        MethodInfo method,
        string packageFullName,
        string packageRoot,
        out UnifySteamGameCacheEntry game)
    {
        var arguments = new object?[] { packageFullName, packageRoot, null, null };
        var result = (bool)method.Invoke(null, arguments)!;
        game = (UnifySteamGameCacheEntry)arguments[3]!;
        return result;
    }

    private static string BuildManifest(bool xboxLive)
    {
        var extension = xboxLive
            ? "<Extensions><Extension Category=\"windows.xboxLive\" /></Extensions>"
            : string.Empty;
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
            "<Identity Name=\"Contoso.TestGame\" Version=\"1.0.0.0\" Publisher=\"CN=Contoso\" />" +
            "<Properties><DisplayName>Test Game</DisplayName></Properties>" +
            "<Applications><Application Id=\"Game\">" +
            "<VisualElements DisplayName=\"Test Game\" />" +
            extension +
            "</Application></Applications>" +
            "</Package>";
    }
}
