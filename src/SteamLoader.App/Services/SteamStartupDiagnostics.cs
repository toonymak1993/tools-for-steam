namespace SteamLoader.App.Services;

internal static class SteamStartupDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "data", "steam-startup.log");
    private static int _writesUntilSizeCheck;

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (_writesUntilSizeCheck-- <= 0)
                {
                    _writesUntilSizeCheck = 31;
                    RotateIfNeeded();
                }

                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaximumLogBytes)
        {
            return;
        }

        var previousPath = LogPath + ".previous";
        File.Move(LogPath, previousPath, overwrite: true);
    }
}
