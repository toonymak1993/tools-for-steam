using System.Text.Json;
using SteamLoader.App.Hosting;
using SteamLoader.App.Infrastructure.Steam;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class FrontendLoaderTests
{
    private static readonly Uri ApiBase = new("http://127.0.0.1:47652/");
    private const string Token = "abcdefghijklmnopqrstuvwxyz0123456789_TOKEN";

    [Fact]
    public void ScriptBundle_KeepsInjectedContractAndHidesToken()
    {
        var registry = new FrontendBundleRegistry(ApiBase, Token);
        var bundle = registry.AddScript(
            "quickaccess",
            "__steamLoaderPopupScriptVersion",
            "(() => { const apiBase = \"__STEAMLOADER_API_BASE__\"; const v = \"__STEAMLOADER_SCRIPT_VERSION__\"; })();");

        Assert.Equal("quickaccess.js", bundle.FileName);
        Assert.False(bundle.IsModule);
        Assert.Contains("http://127.0.0.1:47652/", bundle.Body);
        Assert.Contains(bundle.Version, bundle.Body);
        Assert.EndsWith("\"injected\";", bundle.Body);
        Assert.Contains($"window[\"__steamLoaderPopupScriptVersion\"] = \"{bundle.Version}\";", bundle.Body);
        // The bundle is served without authentication, so it must never carry the session token.
        Assert.DoesNotContain(Token, bundle.Body);
        Assert.True(registry.TryGet("quickaccess.js", out var found));
        Assert.Same(bundle, found);
    }

    [Fact]
    public void BundleVersion_ChangesWithTokenAndContent()
    {
        var first = new FrontendBundleRegistry(ApiBase, Token).AddScript("a", "__v", "x");
        var otherToken = new FrontendBundleRegistry(ApiBase, Token + "2").AddScript("a", "__v", "x");
        var otherContent = new FrontendBundleRegistry(ApiBase, Token).AddScript("a", "__v", "y");

        Assert.NotEqual(first.Version, otherToken.Version);
        Assert.NotEqual(first.Version, otherContent.Version);
        Assert.Equal(16, first.Version.Length);
    }

    [Theory]
    [InlineData("/frontend/quickaccess.js", true, "quickaccess.js")]
    [InlineData("/frontend/tfs-core.mjs", true, "tfs-core.mjs")]
    [InlineData("/frontend/../secret.txt", false, "")]
    [InlineData("/frontend/a/b.js", false, "")]
    [InlineData("/frontend/", false, "")]
    [InlineData("/api/frontend/components", false, "")]
    public void TryParseRequestPath_AcceptsOnlyFlatBundleNames(string path, bool expected, string expectedName)
    {
        Assert.Equal(expected, FrontendBundleRegistry.TryParseRequestPath(path, out var fileName));
        Assert.Equal(expectedName, fileName);
    }

    [Fact]
    public void FrontendBundles_ArePublicReadOnlyResources()
    {
        Assert.True(LocalApiSession.IsPublicResourceRequest("GET", "/frontend/quickaccess.js"));
        Assert.False(LocalApiSession.IsPublicResourceRequest("POST", "/frontend/quickaccess.js"));
    }

    [Fact]
    public void LoaderScript_FetchesClassicBundleAndEvaluatesInGlobalScope()
    {
        var registry = new FrontendBundleRegistry(ApiBase, Token);
        var bundle = registry.AddScript("surfaces", "__steamLoaderThemeSurfaceScriptVersion", "void 0;");
        var loader = QuickAccessShellInjector.BuildLoaderScript(bundle, ApiBase, Token);

        Assert.Contains($"http://127.0.0.1:47652/frontend/surfaces.js?v={bundle.Version}", loader);
        Assert.Contains("(0, eval)(source", loader);
        Assert.Contains(QuickAccessShellInjector.LoaderFetchFailedPrefix, loader);
        Assert.Contains(LocalApiSession.HeaderName, loader);
        // The loader is tiny; the bundle body itself never travels through the debugger.
        Assert.DoesNotContain("void 0;", loader);
        Assert.True(loader.Length < 4000);
    }

    [Fact]
    public void LoaderScript_ImportsModulesAndStartsThem()
    {
        var registry = new FrontendBundleRegistry(ApiBase, Token);
        var bundle = registry.AddModule("tfs-core", "__tfsCoreVersion", "export async function start() {}");
        var loader = QuickAccessShellInjector.BuildLoaderScript(bundle, ApiBase, Token);

        Assert.True(bundle.IsModule);
        Assert.Contains($"await import(\"http://127.0.0.1:47652/frontend/tfs-core.mjs?v={bundle.Version}\")", loader);
        Assert.Contains("module.start", loader);
        Assert.Contains("window[\"__tfsCoreVersion\"]", loader);
    }

    [Fact]
    public void InlineFallback_ShipsTheWholeBundleLikeBefore()
    {
        var registry = new FrontendBundleRegistry(ApiBase, Token);
        var bundle = registry.AddScript("shared", "__steamLoaderSharedScriptVersion", "window.__probe = 1;");
        var inline = QuickAccessShellInjector.BuildInlineScript(bundle, ApiBase, Token);

        Assert.Contains("window.__probe = 1;", inline);
        Assert.EndsWith("\"injected\";", inline);
    }

    [Theory]
    [InlineData("{\"frame\":{\"id\":\"1\",\"url\":\"https://steamloopback.host/index.html\"}}", true)]
    [InlineData("{\"frame\":{\"id\":\"2\",\"parentId\":\"1\",\"url\":\"about:blank\"}}", false)]
    [InlineData("{}", false)]
    public void IsMainFrameNavigation_IgnoresChildFrames(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, QuickAccessShellInjector.IsMainFrameNavigation(document.RootElement));
    }

    [Theory]
    [InlineData("injected", true)]
    [InlineData("waiting", true)]
    [InlineData("tfs-fetch-failed:Failed to fetch", false)]
    [InlineData(null, false)]
    public void IsInjectedValue_AcceptsBundleCompletionValues(string? value, bool expected)
    {
        Assert.Equal(expected, QuickAccessShellInjector.IsInjectedValue(value));
    }

    [Fact]
    public void ReadEvaluationReply_ReadsValuesAndExceptions()
    {
        using var ok = JsonDocument.Parse("{\"id\":3,\"result\":{\"result\":{\"type\":\"string\",\"value\":\"injected\"}}}");
        var okResult = SteamCdpSession.ReadEvaluationReply(ok.RootElement);
        Assert.True(okResult.Success);
        Assert.Equal("injected", okResult.Value?.ToString());

        using var thrown = JsonDocument.Parse(
            "{\"id\":4,\"result\":{\"result\":{\"type\":\"object\"},\"exceptionDetails\":{\"text\":\"Uncaught\",\"exception\":{\"description\":\"ReferenceError: x is not defined\"}}}}");
        var thrownResult = SteamCdpSession.ReadEvaluationReply(thrown.RootElement);
        Assert.False(thrownResult.Success);
        Assert.Equal("ReferenceError: x is not defined", thrownResult.ErrorMessage);

        using var protocolError = JsonDocument.Parse("{\"id\":5,\"error\":{\"code\":-32000,\"message\":\"Target closed\"}}");
        var protocolResult = SteamCdpSession.ReadEvaluationReply(protocolError.RootElement);
        Assert.False(protocolResult.Success);
        Assert.Equal("Target closed", protocolResult.ErrorMessage);
    }
}
