using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.PluginStore;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class LocalApiSessionTests
{
    [Fact]
    public void GetOrCreate_PersistsOneStrongToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "session.token");

        try
        {
            var first = LocalApiSession.GetOrCreate(path);
            var second = LocalApiSession.GetOrCreate(path);

            Assert.Equal(first, second);
            Assert.True(first.Length >= 40);
            Assert.DoesNotContain("=", first);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://steamloopback.host", true)]
    [InlineData("https://steamcommunity.com", true)]
    [InlineData("https://example.com", false)]
    [InlineData("null", true)]
    public void IsTrustedOrigin_OnlyAllowsInternalAndSteamOrigins(string? origin, bool expected)
    {
        Assert.Equal(expected, LocalApiSession.IsTrustedOrigin(origin));
    }

    [Fact]
    public void IsAuthorized_RequiresTokenAndOnlyAllowsQueryTokenForGet()
    {
        const string token = "abcdefghijklmnopqrstuvwxyz0123456789_TOKEN";

        Assert.True(LocalApiSession.IsAuthorized(token, token, null, "POST"));
        Assert.True(LocalApiSession.IsAuthorized(token, null, token, "GET"));
        Assert.False(LocalApiSession.IsAuthorized(token, null, token, "POST"));
        Assert.False(LocalApiSession.IsAuthorized(token, "wrong", null, "GET"));
    }

    [Theory]
    [InlineData("GET", "/health", true)]
    [InlineData("GET", "/api/plugin-store/images/catalog/example.png", true)]
    [InlineData("GET", "/api/plugin-store/community/example/files/dist/index.js", true)]
    [InlineData("GET", "/api/plugin-store/state", false)]
    [InlineData("POST", "/api/plugin-store/community/example/files/dist/index.js", false)]
    public void IsPublicResourceRequest_OnlyAllowsReadOnlyBrowserResources(string method, string path, bool expected)
    {
        Assert.Equal(expected, LocalApiSession.IsPublicResourceRequest(method, path));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.1", false)]
    [InlineData("1.1.0", false)]
    [InlineData("2.0.0", false)]
    [InlineData("invalid", false)]
    public void SupportedSdkVersion_RejectsFutureFeatureLevels(string version, bool expected)
    {
        Assert.Equal(expected, PluginStoreService.IsSupportedSdkVersion(version));
    }
}
