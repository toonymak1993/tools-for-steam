using System.Reflection;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Fact]
    public void ValidateInstallerPackage_AcceptsMinimalMzExecutable()
    {
        var root = CreateTempRoot();

        try
        {
            var packagePath = Path.Combine(root, "ToolsForSteamSetup.exe");
            var bytes = new byte[2048];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            File.WriteAllBytes(packagePath, bytes);

            var method = typeof(ReleaseUpdateService).GetMethod(
                "ValidateInstallerPackage",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            method!.Invoke(null, [packagePath]);
            Assert.True(File.Exists(packagePath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ValidateInstallerPackage_RejectsInvalidExecutableAndDeletesIt()
    {
        var root = CreateTempRoot();

        try
        {
            var packagePath = Path.Combine(root, "ToolsForSteamSetup.exe");
            File.WriteAllBytes(packagePath, new byte[2048]);

            var method = typeof(ReleaseUpdateService).GetMethod(
                "ValidateInstallerPackage",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, [packagePath]));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.False(File.Exists(packagePath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void FindUpdateAsset_RequiresInstallerAsset()
    {
        var serviceType = typeof(ReleaseUpdateService);
        var assetType = serviceType.GetNestedType("GithubReleaseAsset", BindingFlags.NonPublic);
        var releaseType = serviceType.GetNestedType("GithubRelease", BindingFlags.NonPublic);
        var method = serviceType.GetMethod("FindUpdateAsset", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(assetType);
        Assert.NotNull(releaseType);
        Assert.NotNull(method);

        var portableAsset = CreateNonPublicInstance(
            assetType!,
            "ToolsForSteam-portable-win-x64.zip",
            "https://example.invalid/portable.zip");
        var installerAsset = CreateNonPublicInstance(
            assetType!,
            "ToolsForSteamSetup.exe",
            "https://example.invalid/setup.exe");
        var portableOnlyAssets = Array.CreateInstance(assetType!, 1);
        portableOnlyAssets.SetValue(portableAsset, 0);
        var mixedAssets = Array.CreateInstance(assetType!, 2);
        mixedAssets.SetValue(portableAsset, 0);
        mixedAssets.SetValue(installerAsset, 1);

        var portableOnlyRelease = CreateNonPublicInstance(
            releaseType!,
            "v0.3.4-beta.1",
            "0.3.4 beta 1",
            "https://example.invalid/release",
            DateTimeOffset.UtcNow,
            false,
            true,
            portableOnlyAssets);
        var mixedRelease = CreateNonPublicInstance(
            releaseType!,
            "v0.3.4-beta.1",
            "0.3.4 beta 1",
            "https://example.invalid/release",
            DateTimeOffset.UtcNow,
            false,
            true,
            mixedAssets);

        Assert.Null(method!.Invoke(null, [portableOnlyRelease]));

        var matchedAsset = method.Invoke(null, [mixedRelease]);
        Assert.NotNull(matchedAsset);
        Assert.Equal(
            "ToolsForSteamSetup.exe",
            assetType!.GetProperty("Name")?.GetValue(matchedAsset));
    }

    [Fact]
    public void SelectRelease_StableIgnoresPrerelease_WhenStableExists()
    {
        var method = typeof(ReleaseUpdateService).GetMethod("SelectRelease", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var stableRelease = CreateRelease("v0.3.4", prerelease: false, publishedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));
        var prerelease = CreateRelease("v0.3.5-beta.1", prerelease: true, publishedAtUtc: DateTimeOffset.UtcNow);
        var releases = CreateReleaseArray(prerelease, stableRelease);

        var selected = method!.Invoke(null, [releases, "stable"]);
        var tagName = selected?.GetType().GetProperty("TagName")?.GetValue(selected);

        Assert.Equal("v0.3.4", tagName);
    }

    [Fact]
    public void SelectRelease_BetaPrefersHighestPrerelease()
    {
        var method = typeof(ReleaseUpdateService).GetMethod("SelectRelease", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var prerelease1 = CreateRelease("v0.3.4-beta.2", prerelease: true, publishedAtUtc: DateTimeOffset.UtcNow.AddDays(-2));
        var prerelease2 = CreateRelease("v0.3.4-beta.10", prerelease: true, publishedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));
        var stableRelease = CreateRelease("v0.3.4", prerelease: false, publishedAtUtc: DateTimeOffset.UtcNow);
        var releases = CreateReleaseArray(prerelease1, stableRelease, prerelease2);

        var selected = method!.Invoke(null, [releases, "beta"]);
        var tagName = selected?.GetType().GetProperty("TagName")?.GetValue(selected);

        Assert.Equal("v0.3.4-beta.10", tagName);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static object CreateRelease(string tagName, bool prerelease, DateTimeOffset publishedAtUtc)
    {
        var serviceType = typeof(ReleaseUpdateService);
        var releaseType = serviceType.GetNestedType("GithubRelease", BindingFlags.NonPublic);
        var assetType = serviceType.GetNestedType("GithubReleaseAsset", BindingFlags.NonPublic);

        Assert.NotNull(releaseType);
        Assert.NotNull(assetType);

        var installerAsset = CreateNonPublicInstance(
            assetType!,
            "ToolsForSteamSetup.exe",
            $"https://example.invalid/{tagName}/setup.exe");
        var assets = Array.CreateInstance(assetType!, 1);
        assets.SetValue(installerAsset, 0);

        return CreateNonPublicInstance(
            releaseType!,
            tagName,
            tagName,
            $"https://example.invalid/{tagName}",
            publishedAtUtc,
            false,
            prerelease,
            assets);
    }

    private static Array CreateReleaseArray(params object[] releases)
    {
        var releaseType = typeof(ReleaseUpdateService).GetNestedType("GithubRelease", BindingFlags.NonPublic);
        Assert.NotNull(releaseType);

        var array = Array.CreateInstance(releaseType!, releases.Length);
        for (var index = 0; index < releases.Length; index += 1)
        {
            array.SetValue(releases[index], index);
        }

        return array;
    }

    private static object CreateNonPublicInstance(Type type, params object?[] arguments)
    {
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(info => info.GetParameters().Length == arguments.Length);

        return constructor.Invoke(arguments);
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
