using System.Runtime.InteropServices;
using System.Text;
using SteamLoader.App.Infrastructure.Assets;

namespace SteamLoader.App.Infrastructure.Themes;

internal static class WallpaperService
{
    private const string EmbeddedWallpaperAssetPath = "Assets/theme-wallpaper.png";
    private const int SpiGetDeskWallpaper = 0x0073;
    private const int SpiSetDeskWallpaper = 0x0014;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int uAction, int uParam, StringBuilder lpvParam, int fuWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    public static string? GetCurrentWallpaperPath()
    {
        var buffer = new StringBuilder(260);
        SystemParametersInfo(SpiGetDeskWallpaper, buffer.Capacity, buffer, 0);
        var path = buffer.ToString().Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public static string? ApplyTfsWallpaper()
    {
        var targetPath = EnsureWallpaperFileExtracted();
        var currentWallpaperPath = GetCurrentWallpaperPath();
        var previousWallpaperPath = string.Equals(currentWallpaperPath, targetPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : currentWallpaperPath;

        SystemParametersInfo(SpiSetDeskWallpaper, 0, targetPath, SpifUpdateIniFile | SpifSendChange);

        // Best-effort: the lock screen image lives behind a machine-wide policy
        // key, so this needs one short elevated relaunch. If the user declines
        // the UAC prompt, the desktop wallpaper above still applies normally.
        LockScreenWallpaperElevatedWorker.RequestApply(targetPath);

        return previousWallpaperPath;
    }

    public static void RestoreWallpaper(string? previousWallpaperPath)
    {
        if (!string.IsNullOrWhiteSpace(previousWallpaperPath) && File.Exists(previousWallpaperPath))
        {
            SystemParametersInfo(SpiSetDeskWallpaper, 0, previousWallpaperPath, SpifUpdateIniFile | SpifSendChange);
        }

        LockScreenWallpaperElevatedWorker.RequestClear();
    }

    private static string EnsureWallpaperFileExtracted()
    {
        var targetDirectory = Path.Combine(AppContext.BaseDirectory, "data", "theme");
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, "tfs-wallpaper.png");
        File.WriteAllBytes(targetPath, EmbeddedAssetReader.ReadBytes(EmbeddedWallpaperAssetPath));
        return targetPath;
    }
}
