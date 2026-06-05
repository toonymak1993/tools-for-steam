using SteamLoader.App.Infrastructure.Settings;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public sealed class ConsoleModeShellGuardService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RestoreGracePeriod = TimeSpan.FromSeconds(8);

    private readonly SteamDevToolsClient _devToolsClient;
    private readonly SteamLoaderSettingsService _settingsService;
    private readonly WindowsShellVisibilityService _shellVisibilityService;

    public ConsoleModeShellGuardService(
        SteamDevToolsClient devToolsClient,
        SteamLoaderSettingsService settingsService,
        WindowsShellVisibilityService shellVisibilityService)
    {
        _devToolsClient = devToolsClient;
        _settingsService = settingsService;
        _shellVisibilityService = shellVisibilityService;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastBigPictureSeenAt = DateTimeOffset.MinValue;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var shouldHideShell = _settingsService.ShouldHideWindowsShellInConsoleMode()
                    && await IsBigPictureActiveAsync(cancellationToken);

                if (shouldHideShell)
                {
                    lastBigPictureSeenAt = DateTimeOffset.UtcNow;
                    _shellVisibilityService.HideShellChrome();
                }
                else if (
                    _shellVisibilityService.IsHidden &&
                    DateTimeOffset.UtcNow - lastBigPictureSeenAt > RestoreGracePeriod)
                {
                    _shellVisibilityService.RestoreShellChrome();
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            _shellVisibilityService.RestoreShellChrome();
        }
    }

    private async Task<bool> IsBigPictureActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _devToolsClient.HasBigPictureSurfaceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
