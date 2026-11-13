using System.Text.Json;
using SteamLoader.App.Infrastructure.Discord;
using SteamLoader.App.Infrastructure.Steam;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class DiscordWebBridgeTests
{
    [Theory]
    [InlineData("/app", "/app")]
    [InlineData("/channels/@me", "/channels/@me")]
    [InlineData("/channels/123456789012345678", "/channels/123456789012345678")]
    [InlineData("/channels/123456789012345678/223456789012345678", "/channels/123456789012345678/223456789012345678")]
    [InlineData("/users/123456789012345678", "/users/123456789012345678")]
    public void NormalizeAppPath_AllowsDiscordAppPages(string input, string expected)
    {
        Assert.Equal(expected, DiscordWebBridge.NormalizeAppPath(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("channels/123456789012345678")]
    [InlineData("/login")]
    [InlineData("/channels/123")]
    [InlineData("/channels/123456789012345678/x'onload")]
    [InlineData("//evil.example/channels/123456789012345678")]
    [InlineData("/users/@me")]
    [InlineData("/app/settings")]
    public void NormalizeAppPath_RefusesEverythingElse(string? input)
    {
        Assert.Null(DiscordWebBridge.NormalizeAppPath(input));
    }

    [Theory]
    [InlineData("Runtime.bindingCalled", "tfsDiscordKeyboard", "show", "show")]
    [InlineData("Runtime.bindingCalled", "tfsDiscordKeyboard", "hide", "hide")]
    [InlineData("Runtime.bindingCalled", "tfsDiscordKeyboard", "type:hello", null)]
    [InlineData("Runtime.bindingCalled", "otherBinding", "show", null)]
    [InlineData("Runtime.consoleAPICalled", "tfsDiscordKeyboard", "show", null)]
    public void ParseKeyboardRequest_OnlyAcceptsShowAndHide(string method, string name, string payload, string? expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { name, payload, executionContextId = 1 }));
        Assert.Equal(expected, DiscordWebBridge.ParseKeyboardRequest(method, document.RootElement));
    }

    [Fact]
    public void KeyboardHelper_ReportsUserPickedTextFieldsOnly()
    {
        var script = DiscordWebBridge.KeyboardHelperScript;
        Assert.Contains("window.tfsDiscordKeyboard", script);
        Assert.Contains("[contenteditable]", script);
        Assert.Contains("event.isTrusted", script);
        Assert.DoesNotContain("textContent", script);
        Assert.DoesNotContain(".value", script);
    }

    [Fact]
    public void FindDiscordTarget_PicksTheDiscordPageOverSteamSurfaces()
    {
        var targets = new[]
        {
            new SteamDevToolsTarget("1", "SharedJSContext", "page", "https://steamloopback.host/routes/library", "ws://a"),
            new SteamDevToolsTarget("2", "Discord", "page", "https://discord.com/login", "ws://b"),
            new SteamDevToolsTarget("3", "Discord", "page", "https://discord.com/channels/@me", "ws://c"),
            new SteamDevToolsTarget("4", "fake", "page", "https://discord.com.evil.example/channels/1", "ws://d"),
            new SteamDevToolsTarget("5", "worker", "service_worker", "https://discord.com/assets/sw.js", "ws://e"),
        };

        Assert.Equal("3", DiscordWebBridge.FindDiscordTarget(targets)?.Id);
        Assert.Null(DiscordWebBridge.FindDiscordTarget(targets.Take(1).ToArray()));
    }

    [Theory]
    [InlineData("https://discord.com/app", true)]
    [InlineData("https://ptb.discord.com/channels/@me", true)]
    [InlineData("http://discord.com/app", false)]
    [InlineData("https://discord.gg/abc", false)]
    [InlineData("https://notdiscord.com/app", false)]
    public void IsDiscordUrl_OnlyAcceptsDiscordOverHttps(string url, bool expected)
    {
        Assert.Equal(expected, DiscordWebBridge.IsDiscordUrl(url));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]
    [InlineData(2, 1, true)]
    [InlineData(1, 0, true)]
    public void Back_IsHandledOnlyWhenAPopupClosed(int before, int after, bool expected)
    {
        Assert.Equal(expected, DiscordWebBridge.DecideBackHandled(before, after));
    }

    [Fact]
    public void ParseState_ReadsVoiceAndSignInWithoutGuessing()
    {
        using var document = JsonDocument.Parse("""
            {"signedIn":true,"voiceConnected":true,"muted":false,"deafened":null,"guildId":"123456789012345678","path":"/channels/123456789012345678","overlays":1}
            """);

        var state = DiscordWebBridge.ParseState(document.RootElement.Clone(), "https://discord.com/app");

        Assert.True(state.Running);
        Assert.True(state.SignedIn);
        Assert.True(state.VoiceConnected);
        Assert.False(state.Muted);
        Assert.Null(state.Deafened);
        Assert.Equal("123456789012345678", state.GuildId);
        Assert.Equal(1, state.Overlays);

        var loading = DiscordWebBridge.ParseState(null, "https://discord.com/login");
        Assert.True(loading.Running);
        Assert.Null(loading.SignedIn);
        Assert.Equal("/login", loading.Path);
    }

    [Fact]
    public void Shortcuts_AreAFixedWhitelist()
    {
        Assert.Contains("toggle-mute", DiscordWebBridge.Shortcuts.Keys);
        Assert.Contains("quick-switcher", DiscordWebBridge.Shortcuts.Keys);
        Assert.Equal(2 | 1, DiscordWebBridge.Shortcuts["next-server"].Modifiers);
        Assert.DoesNotContain(DiscordWebBridge.Shortcuts.Keys, key => key.Contains("enter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PageHelper_ReadsNoMessagesOrCredentials()
    {
        var script = DiscordWebBridge.PageHelperScript;
        Assert.DoesNotContain("innerText", script);
        Assert.DoesNotContain("localStorage", script);
        Assert.DoesNotContain("token", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".value", script);
    }
}
