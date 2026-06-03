using SteamLoader.App;
using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.Settings;

public sealed class SteamLoaderSettingsService
{
    private readonly WindowsAutostartService _autostartService;
    private readonly WindowsShellService _shellService;
    private readonly string _executablePath;
    private readonly string _shellLaunchArguments;

    public SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        WindowsShellService shellService,
        string executablePath,
        string shellLaunchArguments)
    {
        _autostartService = autostartService;
        _shellService = shellService;
        _executablePath = executablePath;
        _shellLaunchArguments = shellLaunchArguments;
    }

    public SteamLoaderGeneralSettingsSnapshot GetSnapshot()
    {
        return new SteamLoaderGeneralSettingsSnapshot(
            RunOnWindowsSignIn: _shellService.IsEnabled(_executablePath, _shellLaunchArguments));
    }

    public SteamLoaderGeneralSettingsSnapshot SetRunOnWindowsSignIn(bool enabled)
    {
        if (enabled)
        {
            _autostartService.DisableSteamAutostartEntries();
        }

        _autostartService.SetEnabled(_executablePath, SteamLoaderRuntime.AutostartArguments, false);
        _shellService.SetEnabled(_executablePath, _shellLaunchArguments, enabled);
        return GetSnapshot();
    }
}
