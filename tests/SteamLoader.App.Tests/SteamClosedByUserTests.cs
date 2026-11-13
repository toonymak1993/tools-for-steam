using System.Net;
using SteamLoader.App.Infrastructure.Steam;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

/// <summary>
/// luis (2026-11-10): when the user closes Steam, Tools for Steam must not
/// start it again. It waits until Steam and Big Picture are back and reconnects.
/// </summary>
public sealed class SteamClosedByUserTests
{
    [Theory]
    [InlineData(false)] // Steam fully closed.
    [InlineData(true)]  // steam.exe still shutting down, or started outside TFS.
    public async Task AfterAttach_ClosingSteamNeverStartsOrRestartsIt(bool steamProcessStillRunning)
    {
        var steam = new FakeSteam { EndpointUp = true, UiVisible = true };
        var service = steam.CreateService();

        var attached = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
        Assert.True(attached.DevToolsReady);

        steam.EndpointUp = false;
        steam.UiVisible = false;
        steam.ProcessRunning = steamProcessStillRunning;

        // Poll far longer than every grace period and recovery cooldown would allow.
        for (var poll = 0; poll < 50; poll++)
        {
            var state = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
            Assert.False(state.DevToolsReady);
            Assert.NotEqual(SteamClientStartupStage.Recovering, state.StartupStage);
        }

        // Nothing that could start, restart or kill Steam was even prepared.
        Assert.Equal(0, steam.ExecutableLookups);
    }

    [Fact]
    public async Task BeforeAttach_SteamClosingDuringStartupIsNeverRelaunched()
    {
        // Steam is running (started by the console start) but DevTools is not up yet.
        var steam = new FakeSteam { EndpointUp = false, UiVisible = false, ProcessRunning = true };
        var service = steam.CreateService();
        await service.EnsureDevToolsReadyAsync(CancellationToken.None);
        var lookupsBeforeClose = steam.ExecutableLookups;

        // The user quits Steam, or Steam restarts itself after an update.
        steam.ProcessRunning = false;
        for (var poll = 0; poll < 50; poll++)
        {
            var state = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
            Assert.False(state.DevToolsReady);
            Assert.NotEqual(SteamClientStartupStage.Recovering, state.StartupStage);
        }

        // Nothing that could start or kill Steam was prepared after it closed.
        Assert.Equal(lookupsBeforeClose, steam.ExecutableLookups);

        // Steam comes back on its own: TFS attaches without touching it.
        steam.ProcessRunning = true;
        steam.EndpointUp = true;
        steam.UiVisible = true;
        var ready = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
        Assert.True(ready.DevToolsReady);
        Assert.Equal(SteamClientStartupStage.Ready, ready.StartupStage);
    }

    [Fact]
    public async Task WhenSteamComesBack_TfsReconnectsWithoutPushingBigPicture()
    {
        var steam = new FakeSteam { EndpointUp = true, UiVisible = true };
        var service = steam.CreateService();
        await service.EnsureDevToolsReadyAsync(CancellationToken.None);

        steam.EndpointUp = false;
        steam.UiVisible = false;
        await service.EnsureDevToolsReadyAsync(CancellationToken.None);

        // The user starts Steam again; its window is not up yet.
        steam.EndpointUp = true;
        steam.ProcessRunning = true;
        for (var poll = 0; poll < 20; poll++)
        {
            var state = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
            Assert.True(state.DevToolsReady);
        }

        steam.UiVisible = true;
        var ready = await service.EnsureDevToolsReadyAsync(CancellationToken.None);
        Assert.True(ready.DevToolsReady);
        Assert.Equal(SteamClientStartupStage.Ready, ready.StartupStage);
        Assert.Equal(0, steam.ExecutableLookups);
    }

    private sealed class FakeSteam
    {
        public bool EndpointUp { get; set; }

        public bool UiVisible { get; set; }

        public bool ProcessRunning { get; set; } = true;

        public int ExecutableLookups { get; private set; }

        public SteamClientLaunchService CreateService()
        {
            var root = Path.Combine(Path.GetTempPath(), "steamloader-tests", Guid.NewGuid().ToString("N"));
            var installation = new SteamInstallationService(
                new SteamInstallPathSettingsStore(Path.Combine(root, "steam-install-path.json")),
                Path.Combine(root, "Steam"));
            return new SteamClientLaunchService(
                new HttpClient(new EndpointHandler(this)),
                new Uri("http://127.0.0.1:65530"),
                installation)
            {
                SteamRunningProbe = () => ProcessRunning,
                SteamUiVisibleProbe = () => UiVisible,
                // Returning no executable makes every start/restart path bail
                // out, so even a regression could never touch the real Steam.
                SteamExecutableProbe = () =>
                {
                    ExecutableLookups++;
                    return null;
                }
            };
        }

        private sealed class EndpointHandler(FakeSteam steam) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(
                    steam.EndpointUp ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
            }
        }
    }
}
