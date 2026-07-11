namespace SteamLoader.App.Services;

internal static class SteamStartupDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "data", "steam-startup.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
