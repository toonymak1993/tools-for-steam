using System.Reflection;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Fact]
    public void BuildInstallerArguments_MarksUpdateAndPersistsInstallerLog()
    {
        var method = typeof(ReleaseUpdateService).GetMethod(
            "BuildInstallerArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var arguments = Assert.IsType<string>(method!.Invoke(null, [@"C:\Program Files\ToolsForSteam"]));

        Assert.Contains("/VERYSILENT", arguments, StringComparison.Ordinal);
        Assert.Contains("/TFSUPDATE=1", arguments, StringComparison.Ordinal);
        Assert.Contains("/DIR=\"C:\\Program Files\\ToolsForSteam\"", arguments, StringComparison.Ordinal);
        Assert.Contains("/LOG=\"C:\\Program Files\\ToolsForSteam\\data\\installer-update.log\"", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUpdateScript_LogsInstallerFailureAndRestartsExistingVersion()
    {
        var method = typeof(ReleaseUpdateService).GetMethod(
            "BuildUpdateScript",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var script = Assert.IsType<string>(method!.Invoke(
            null,
            [
                @"C:\Temp\TfsUpdate",
                @"C:\Temp\TfsUpdate\ToolsForSteamSetup.exe",
                @"C:\Program Files\ToolsForSteam",
                "ToolsForSteam.exe",
                new[] { 10, 20 },
                true
            ]));

        Assert.Contains("data\\update-handoff.log", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $existingExe -ArgumentList \"--tray\"", script, StringComparison.Ordinal);
        Assert.Contains("$installer.ExitCode", script, StringComparison.Ordinal);
    }

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
            "https://example.invalid/portable.zip",
            null);
        var installerAsset = CreateNonPublicInstance(
            assetType!,
            "ToolsForSteamSetup.exe",
            "https://example.invalid/setup.exe",
            null);
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

    // A fixed point inside the current version line (see VersionLineStartUtc).
    private static readonly DateTimeOffset ReleaseNow = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectLatestStableRelease_IgnoresPrereleases()
    {
        var stableRelease = CreateRelease("v0.2.1", prerelease: false, publishedAtUtc: ReleaseNow.AddDays(-1));
        var prerelease = CreateRelease("v0.2.2-beta.1", prerelease: true, publishedAtUtc: ReleaseNow);

        Assert.Equal("v0.2.1", SelectLatestStableTag(prerelease, stableRelease));
    }

    [Fact]
    public void SelectLatestStableRelease_IgnoresBetaTagWithoutPrereleaseFlag()
    {
        // Mirrors the published v0.3.9-beta.1, which lacks GitHub's prerelease flag.
        var mislabeledBeta = CreateRelease("v0.2.2-beta.1", prerelease: false, publishedAtUtc: ReleaseNow);
        var stableRelease = CreateRelease("v0.2.1", prerelease: false, publishedAtUtc: ReleaseNow.AddDays(-1));

        Assert.Equal("v0.2.1", SelectLatestStableTag(mislabeledBeta, stableRelease));
    }

    [Fact]
    public void SelectLatestStableRelease_PrefersHighestVersionOverPublishDate()
    {
        var older = CreateRelease("v0.2.10", prerelease: false, publishedAtUtc: ReleaseNow.AddDays(-5));
        var newerDateLowerVersion = CreateRelease("v0.2.9", prerelease: false, publishedAtUtc: ReleaseNow);

        Assert.Equal("v0.2.10", SelectLatestStableTag(newerDateLowerVersion, older));
    }

    [Fact]
    public void SelectLatestStableRelease_SkipsNewerReleaseWithoutInstaller()
    {
        var withoutInstaller = CreateRelease("v0.3.0", prerelease: false, publishedAtUtc: ReleaseNow, withInstaller: false);
        var installable = CreateRelease("v0.2.1", prerelease: false, publishedAtUtc: ReleaseNow.AddDays(-1));

        Assert.Equal("v0.2.1", SelectLatestStableTag(withoutInstaller, installable));
    }

    [Fact]
    public void SelectLatestStableRelease_IgnoresReleasesOfTheRetiredVersionLine()
    {
        // v0.4.0 was published before the version restart at 0.2.0 and must
        // never be offered to a 0.2.x install although its number is higher.
        var retired = CreateRelease("v0.4.0", prerelease: false, publishedAtUtc: new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        var current = CreateRelease("v0.2.0", prerelease: false, publishedAtUtc: ReleaseNow);

        Assert.Equal("v0.2.0", SelectLatestStableTag(retired, current));
    }

    [Fact]
    public void SelectLatestStableRelease_ThrowsWhenOnlyPrereleasesExist()
    {
        var method = typeof(ReleaseUpdateService).GetMethod("SelectLatestStableRelease", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var releases = CreateReleaseArray(CreateRelease("v0.2.1-beta.1", prerelease: true, publishedAtUtc: ReleaseNow));

        var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, [releases]));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void SelectLatestStableRelease_ThrowsWhenOnlyRetiredReleasesExist()
    {
        var method = typeof(ReleaseUpdateService).GetMethod("SelectLatestStableRelease", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var releases = CreateReleaseArray(
            CreateRelease("v0.4.0", prerelease: false, publishedAtUtc: new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)));

        var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, [releases]));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Theory]
    [InlineData("0.4.1", "0.4.1-beta.1", true)]
    [InlineData("0.4.0", "0.4.1-beta.1", false)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("0.3.10", "0.3.9", true)]
    public void IsNewerVersion_NeverDowngradesOrReinstalls(string latest, string current, bool expected)
    {
        var method = typeof(ReleaseUpdateService).GetMethod("IsNewerVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(expected, method!.Invoke(null, [latest, current]));
    }

    [Fact]
    public void ValidatePackageDigest_AcceptsMatchingAndRejectsWrongChecksum()
    {
        var root = CreateTempRoot();

        try
        {
            var method = typeof(ReleaseUpdateService).GetMethod("ValidatePackageDigest", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var packagePath = Path.Combine(root, "ToolsForSteamSetup.exe");
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(packagePath, bytes);
            var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            method!.Invoke(null, [packagePath, digest]);
            method.Invoke(null, [packagePath, null]);
            Assert.True(File.Exists(packagePath));

            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, [packagePath, "sha256:" + new string('0', 64)]));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.False(File.Exists(packagePath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string? SelectLatestStableTag(params object[] releases)
    {
        var method = typeof(ReleaseUpdateService).GetMethod("SelectLatestStableRelease", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var selected = method!.Invoke(null, [CreateReleaseArray(releases)]);
        return selected?.GetType().GetProperty("TagName")?.GetValue(selected) as string;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static object CreateRelease(
        string tagName,
        bool prerelease,
        DateTimeOffset publishedAtUtc,
        bool withInstaller = true)
    {
        var serviceType = typeof(ReleaseUpdateService);
        var releaseType = serviceType.GetNestedType("GithubRelease", BindingFlags.NonPublic);
        var assetType = serviceType.GetNestedType("GithubReleaseAsset", BindingFlags.NonPublic);

        Assert.NotNull(releaseType);
        Assert.NotNull(assetType);

        var installerAsset = CreateNonPublicInstance(
            assetType!,
            withInstaller ? "ToolsForSteamSetup.exe" : "notes.txt",
            $"https://example.invalid/{tagName}/setup.exe",
            null);
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
