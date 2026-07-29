using SteamLoader.App.Infrastructure.SystemTools;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SystemToolsTests
{
    [Fact]
    public void HdrSnapshot_ReadsActiveDisplayStateWithoutThrowing()
    {
        var snapshot = new HdrDisplayService().GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Displays);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.StatusText));
    }

    [Fact]
    public void WindowsUpdateStartInfo_UsesOfficialSettingsAction()
    {
        var startInfo = WindowsSystemUpdateService.CreateWindowsUpdateStartInfo();

        Assert.Equal("ms-settings:windowsupdate-action", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void BluetoothInitialSnapshot_DoesNotStartBackgroundDiscovery()
    {
        using var service = new BluetoothDeviceService();

        var snapshot = service.GetSnapshot();

        Assert.False(snapshot.Scanning);
        Assert.Empty(snapshot.Devices);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.StatusText));
    }

    [Fact]
    public void BluetoothSettingsStartInfo_UsesWindowsSettingsUri()
    {
        var startInfo = BluetoothDeviceService.CreateSettingsStartInfo();

        Assert.Equal("ms-settings:bluetooth", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
    }
}
