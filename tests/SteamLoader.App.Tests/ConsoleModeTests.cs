using Microsoft.Win32;
using SteamLoader.App.Services;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ConsoleModeTests
{
    private const string ExecutablePath = @"C:\ToolsForSteam\ToolsForSteam.exe";

    // Shape of what Task Scheduler stores for the task ConsoleStartTaskService
    // registers (captured from Windows 11 with a disabled probe task).
    private const string RegisteredTaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>probe</Description>
            <URI>\Tools for Steam Console Start - luis</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1-2-3-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <Priority>4</Priority>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Triggers>
            <LogonTrigger>
              <UserId>AYO\luis</UserId>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>C:\ToolsForSteam\ToolsForSteam.exe</Command>
              <Arguments>--tray --console-start --startup-sync</Arguments>
              <WorkingDirectory>C:\ToolsForSteam</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void ConsoleStartTask_AcceptsTheTaskItRegisters()
    {
        Assert.True(
            ConsoleStartTaskService.IsCompatibleTaskXml(
                RegisteredTaskXml.Trim(),
                ExecutablePath,
                SteamLoaderRuntime.ConsoleLaunchArguments,
                out var issue),
            issue);
    }

    [Theory]
    [InlineData(@"<Command>C:\ToolsForSteam\ToolsForSteam.exe</Command>", @"<Command>D:\Old\ToolsForSteam.exe</Command>")]
    [InlineData("<Arguments>--tray --console-start --startup-sync</Arguments>", "<Arguments>--tray</Arguments>")]
    [InlineData("<Priority>4</Priority>", "<Priority>7</Priority>")]
    [InlineData("<Priority>4</Priority>", "")]
    [InlineData("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", "")]
    [InlineData("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", "<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>")]
    [InlineData("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>")]
    [InlineData("<LogonType>InteractiveToken</LogonType>", "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>")]
    [InlineData("<LogonTrigger>", "<BootTrigger>")]
    public void ConsoleStartTask_RejectsTasksThatWouldNotStartConsoleModeCorrectly(string original, string replacement)
    {
        var xml = RegisteredTaskXml.Trim().Replace(original, replacement);
        if (original == "<LogonTrigger>")
        {
            xml = xml.Replace("</LogonTrigger>", "</BootTrigger>");
        }

        Assert.False(ConsoleStartTaskService.IsCompatibleTaskXml(
            xml,
            ExecutablePath,
            SteamLoaderRuntime.ConsoleLaunchArguments,
            out var issue));
        Assert.False(string.IsNullOrWhiteSpace(issue));
    }

    [Fact]
    public void ConsoleStartTask_RejectsADisabledTask()
    {
        var xml = RegisteredTaskXml.Trim().Replace(
            "<ExecutionTimeLimit>",
            "<Enabled>false</Enabled><ExecutionTimeLimit>");

        Assert.False(ConsoleStartTaskService.IsCompatibleTaskXml(
            xml,
            ExecutablePath,
            SteamLoaderRuntime.ConsoleLaunchArguments,
            out _));
    }

    [Theory]
    [InlineData(@"AYO\luisg", "Tools for Steam Console Start - luisg")]
    [InlineData("luisg", "Tools for Steam Console Start - luisg")]
    [InlineData(@"DOMAIN\a:b*c", "Tools for Steam Console Start - abc")]
    [InlineData(@"DOMAIN\", "Tools for Steam Console Start")]
    public void ConsoleStartTask_NamesTheTaskPerUser(string userId, string expected)
    {
        Assert.Equal(expected, ConsoleStartTaskService.BuildTaskName(userId));
    }

    [Theory]
    // Big Picture up, no desktop request: hide.
    [InlineData(true, true, false, false, false, "Hide")]
    // The user asked for the desktop: show the taskbar at once, even with Big Picture up.
    [InlineData(true, true, true, true, false, "Restore")]
    // Big Picture just vanished: wait for the grace period (Steam restarts its UI).
    [InlineData(true, false, false, true, false, "None")]
    [InlineData(true, false, false, true, true, "Restore")]
    // Hiding turned off or eTray: restore whatever TFS hid, without delay.
    [InlineData(false, false, false, true, false, "Restore")]
    // Nothing hidden and nothing to hide.
    [InlineData(true, false, false, false, true, "None")]
    public void ShellGuard_DecidesFromTheCurrentState(
        bool hideEnabled,
        bool bigPictureActive,
        bool desktopRequested,
        bool chromeHidden,
        bool graceElapsed,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<ShellChromeAction>(expected),
            ConsoleModeShellGuardService.Decide(hideEnabled, bigPictureActive, desktopRequested, chromeHidden, graceElapsed));
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(null, false, false)]
    [InlineData(0, true, true)]
    public void XboxModeDetector_SeesSignInStartAndRunningSession(int? startupToGamingHome, bool sessionActive, bool expected)
    {
        var keyPath = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\GamingConfiguration";

        try
        {
            if (startupToGamingHome is not null)
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                key!.SetValue("StartupToGamingHome", startupToGamingHome.Value, RegistryValueKind.DWord);
            }

            var detector = new WindowsXboxModeDetector(keyPath, () => sessionActive);

            Assert.Equal(expected, detector.IsEnabled());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                keyPath[..keyPath.LastIndexOf('\\')],
                throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void XboxModeConflict_NotifiesOncePerConflictAndOnlyWhenItCanBeSeen()
    {
        var tracker = new SteamLoader.App.Hosting.XboxModeConflictTracker();

        // Big Picture overlay not connected yet: wait instead of losing the notice.
        Assert.False(tracker.ShouldNotify(conflict: true, canShow: false));
        Assert.True(tracker.ShouldNotify(conflict: true, canShow: true));
        Assert.False(tracker.ShouldNotify(conflict: true, canShow: true));

        // Turned off and on again: tell the user again.
        Assert.False(tracker.ShouldNotify(conflict: false, canShow: true));
        Assert.True(tracker.ShouldNotify(conflict: true, canShow: true));
    }

    [Fact]
    public void ShellGuard_LeavesTheTaskbarToTheConsoleStartSplash()
    {
        // Big Picture is not up yet and the splash hid the taskbar: the guard
        // must not show it over the splash, grace period or not.
        Assert.Equal(
            ShellChromeAction.None,
            ConsoleModeShellGuardService.Decide(
                hideEnabled: true,
                bigPictureActive: false,
                desktopRequested: false,
                chromeHidden: true,
                restoreGraceElapsed: true,
                startCoverActive: true));
        Assert.Equal(
            ShellChromeAction.Hide,
            ConsoleModeShellGuardService.Decide(
                hideEnabled: true,
                bigPictureActive: true,
                desktopRequested: false,
                chromeHidden: true,
                restoreGraceElapsed: false,
                startCoverActive: true));
    }

    [Fact]
    public void ConsoleShellState_StartCoverOwnerCountsOnlyWhileThatProcessLives()
    {
        var keyPath = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\State";

        try
        {
            var state = new ConsoleShellState(keyPath);
            Assert.False(state.IsStartCoverActive());

            using (var current = System.Diagnostics.Process.GetCurrentProcess())
            {
                state.SetStartCoverOwner(current);
            }

            Assert.True(state.IsStartCoverActive());

            state.ClearStartCoverOwner();
            Assert.False(state.IsStartCoverActive());

            // A dead or reused process id does not count.
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key!.SetValue("ConsoleStartCoverOwner", $"{Environment.ProcessId}:1", RegistryValueKind.String);
            }

            Assert.False(state.IsStartCoverActive());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                keyPath[..keyPath.LastIndexOf('\\')],
                throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void DesktopRequest_StaysUntilBigPictureComesBackToTheFront()
    {
        var tracker = new ConsoleDesktopRequestTracker();
        var requestedAt = DateTimeOffset.UtcNow;

        // Requested from the Quick Access menu while Big Picture is still in front.
        Assert.True(tracker.IsDesktopActive(requestedAt.AddSeconds(1), requestedAt, bigPictureForeground: true, out var ended));
        Assert.False(ended);

        // Big Picture got minimized: the user is on the desktop, for as long as they like.
        Assert.True(tracker.IsDesktopActive(requestedAt.AddSeconds(2), requestedAt, bigPictureForeground: false, out ended));
        Assert.True(tracker.IsDesktopActive(requestedAt.AddHours(2), requestedAt, bigPictureForeground: false, out ended));
        Assert.False(ended);

        // Guide button: Big Picture is back in front, the console takes over again.
        Assert.False(tracker.IsDesktopActive(requestedAt.AddHours(2).AddSeconds(2), requestedAt, bigPictureForeground: true, out ended));
        Assert.True(ended);
    }

    [Fact]
    public void DesktopRequest_EndsWhenBigPictureNeverLeftTheFront()
    {
        var tracker = new ConsoleDesktopRequestTracker();
        var requestedAt = DateTimeOffset.UtcNow;

        Assert.True(tracker.IsDesktopActive(requestedAt.AddSeconds(3), requestedAt, bigPictureForeground: true, out _));
        Assert.False(tracker.IsDesktopActive(
            requestedAt + ConsoleDesktopRequestTracker.UnconfirmedRequestTimeout,
            requestedAt,
            bigPictureForeground: true,
            out var ended));
        Assert.True(ended);
    }

    [Fact]
    public void DesktopRequest_ANewRequestStartsOver()
    {
        var tracker = new ConsoleDesktopRequestTracker();
        var first = DateTimeOffset.UtcNow;
        tracker.IsDesktopActive(first.AddSeconds(1), first, bigPictureForeground: false, out _);

        var second = first.AddMinutes(5);
        Assert.True(tracker.IsDesktopActive(second.AddSeconds(1), second, bigPictureForeground: true, out var ended));
        Assert.False(ended);
    }

    [Fact]
    public void DesktopRequest_NoRequestMeansConsole()
    {
        var tracker = new ConsoleDesktopRequestTracker();

        Assert.False(tracker.IsDesktopActive(DateTimeOffset.UtcNow, request: null, bigPictureForeground: false, out var ended));
        Assert.False(ended);
    }

    [Fact]
    public void ConsoleShellState_SharesDesktopRequestAndHiddenMarkerAcrossInstances()
    {
        var keyPath = $@"Software\SteamLoaderTests\{Guid.NewGuid():N}\State";

        try
        {
            var writer = new ConsoleShellState(keyPath);
            var reader = new ConsoleShellState(keyPath);
            var requestedAt = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

            Assert.Null(reader.GetDesktopRequest());
            Assert.False(reader.IsChromeMarkedHidden());

            writer.RequestDesktop(requestedAt);
            writer.MarkChromeHidden(true);

            Assert.Equal(requestedAt, reader.GetDesktopRequest());
            Assert.True(reader.IsChromeMarkedHidden());

            writer.ClearDesktopRequest();
            writer.MarkChromeHidden(false);

            Assert.Null(reader.GetDesktopRequest());
            Assert.False(reader.IsChromeMarkedHidden());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                keyPath[..keyPath.LastIndexOf('\\')],
                throwOnMissingSubKey: false);
        }
    }
}
