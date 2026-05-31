using SteamLoader.App.Models;
using SteamLoader.App.Services;

namespace SteamLoader.App.Infrastructure.Settings;

public sealed class SteamLoaderSettingsService
{
    private readonly WindowsAutostartService _autostartService;
    private readonly string _executablePath;
    private readonly string _startupArguments;

    public SteamLoaderSettingsService(
        WindowsAutostartService autostartService,
        string executablePath,
        string startupArguments)
    {
        _autostartService = autostartService;
        _executablePath = executablePath;
        _startupArguments = startupArguments;
    }

    public SteamLoaderGeneralSettingsSnapshot GetSnapshot()
    {
        return new SteamLoaderGeneralSettingsSnapshot(
            RunOnWindowsSignIn: _autostartService.IsEnabled(_executablePath, _startupArguments));
    }

    public SteamLoaderGeneralSettingsSnapshot SetRunOnWindowsSignIn(bool enabled)
    {
        if (enabled)
        {
            _autostartService.DisableSteamAutostartEntries();
        }

        _autostartService.SetEnabled(_executablePath, _startupArguments, enabled);
        return GetSnapshot();
    }
}
