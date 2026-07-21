using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Models;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class ProcessWindowServiceLaunchFocusTests
{
    [Fact]
    public void SelectLaunchedAppWindow_PrefersReturnedProcessId()
    {
        var before = new[] { Window("0x1", "Steam", "steamwebhelper", 10, foreground: true) };
        var current = new[]
        {
            Window("0x2", "Unrelated", "other", 20),
            Window("0x3", "Target", "target", 30)
        };

        var selected = ProcessWindowService.SelectLaunchedAppWindow(
            "Target",
            null,
            30,
            before,
            current);

        Assert.Equal("0x3", selected?.Handle);
    }

    [Fact]
    public void SelectLaunchedAppWindow_FindsAlreadyRunningShortcutTarget()
    {
        var before = new[]
        {
            Window("0x1", "Steam", "steamwebhelper", 10, foreground: true),
            Window("0x2", "A project - Visual Studio Code", "Code", 20)
        };

        var selected = ProcessWindowService.SelectLaunchedAppWindow(
            "Visual Studio Code",
            "Code",
            null,
            before,
            before);

        Assert.Equal("0x2", selected?.Handle);
    }

    [Fact]
    public void SelectLaunchedAppWindow_UsesDisplayNameForPackagedApps()
    {
        var before = new[] { Window("0x1", "Steam", "steamwebhelper", 10, foreground: true) };
        var current = new[]
        {
            before[0],
            Window("0x4", "Calculator", "CalculatorApp", 40)
        };

        var selected = ProcessWindowService.SelectLaunchedAppWindow(
            "Calculator",
            null,
            null,
            before,
            current);

        Assert.Equal("0x4", selected?.Handle);
    }

    [Fact]
    public void SelectLaunchedAppWindow_DoesNotReuseUnrelatedPreviousWindow()
    {
        var before = new[]
        {
            Window("0x1", "Steam", "steamwebhelper", 10, foreground: true),
            Window("0x2", "Notes", "notepad", 20)
        };

        var selected = ProcessWindowService.SelectLaunchedAppWindow(
            "Calculator",
            null,
            null,
            before,
            before);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectLaunchedAppWindow_DelaysNameOnlyMatchForExistingWindow()
    {
        var before = new[]
        {
            Window("0x1", "Steam", "steamwebhelper", 10, foreground: true),
            Window("0x2", "Calculator", "CalculatorApp", 20)
        };

        var selected = ProcessWindowService.SelectLaunchedAppWindow(
            "Calculator",
            null,
            null,
            before,
            before,
            allowExistingNameMatch: false);

        Assert.Null(selected);
    }

    private static ProcessWindowInfo Window(
        string handle,
        string title,
        string processName,
        int processId,
        bool foreground = false) =>
        new(handle, title, processName, processId, IsMinimized: false, IsForeground: foreground);
}
