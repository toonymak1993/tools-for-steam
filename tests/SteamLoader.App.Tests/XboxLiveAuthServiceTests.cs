using System.Text.Json;
using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class XboxLiveAuthServiceTests
{
    [Fact]
    public void CreateAuthorizationRequest_UsesPkceAndOrbitPublicClient()
    {
        var request = XboxLiveAuthService.CreateAuthorizationRequest();
        var uri = new Uri(request.Url);

        Assert.Equal("login.microsoftonline.com", uri.Host);
        Assert.Equal(XboxLiveAuthService.ClientId, Query(uri, "client_id"));
        Assert.Equal(XboxLiveAuthService.RedirectUri, Query(uri, "redirect_uri"));
        Assert.Equal(XboxLiveAuthService.Scope, Query(uri, "scope"));
        Assert.Equal("S256", Query(uri, "code_challenge_method"));
        Assert.Equal(request.State, Query(uri, "state"));
        Assert.NotEmpty(Query(uri, "code_challenge"));
        Assert.True(request.CodeVerifier.Length >= 43);
    }

    [Fact]
    public void CreateAuthorizationRequest_UsesFreshStateAndVerifier()
    {
        var first = XboxLiveAuthService.CreateAuthorizationRequest();
        var second = XboxLiveAuthService.CreateAuthorizationRequest();

        Assert.NotEqual(first.State, second.State);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient", true)]
    [InlineData("https://account.live.com/", true)]
    [InlineData("https://login.microsoftonline.com.evil.invalid/", false)]
    [InlineData("http://login.microsoftonline.com/", false)]
    public void IsTrustedLoginUri_OnlyAllowsMicrosoftHttpsDomains(
        string value,
        bool expected)
    {
        Assert.Equal(expected, XboxLiveAuthService.IsTrustedLoginUri(new Uri(value)));
    }

    [Fact]
    public void SettingsStore_EncryptsXboxRefreshTokenAtRest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "tfs-xbox-auth-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "store-sync.json");
        const string refreshToken = "xbox-refresh-token-that-must-never-be-plaintext";
        try
        {
            var store = new StoreSyncSettingsStore(path);
            store.Update(configuration =>
                configuration.UnifySteam.GameData.Providers["xbox-live"]
                    .SecondaryCredential = refreshToken);

            var storedJson = File.ReadAllText(path);
            Assert.DoesNotContain(refreshToken, storedJson, StringComparison.Ordinal);
            Assert.Contains("dpapi:v1:", storedJson, StringComparison.Ordinal);
            Assert.Equal(
                refreshToken,
                store.Load().UnifySteam.GameData.Providers["xbox-live"]
                    .SecondaryCredential);
            using var document = JsonDocument.Parse(storedJson);
            Assert.NotEqual(
                refreshToken,
                document.RootElement
                    .GetProperty("unifySteam")
                    .GetProperty("gameData")
                    .GetProperty("providers")
                    .GetProperty("xbox-live")
                    .GetProperty("secondaryCredential")
                    .GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string Query(Uri uri, string name) =>
        XboxLiveAuthService.ReadQueryValue(uri, name);
}
