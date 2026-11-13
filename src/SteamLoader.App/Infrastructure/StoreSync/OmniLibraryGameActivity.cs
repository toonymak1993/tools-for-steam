using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Tells background OmniLibrary work whether a game is running, so catalog
/// checks, shortcut writes and artwork downloads wait until the player is
/// back in Steam. A Steam game is reported by Steam itself; an OmniLibrary
/// game keeps a named marker open for as long as its launcher process runs.
/// </summary>
internal static class OmniLibraryGameActivity
{
    internal const string SessionMarkerName = @"Local\ToolsForSteamOmniLibraryGameSession";

    /// <summary>
    /// Opens the session marker for the lifetime of an OmniLibrary launch.
    /// Several concurrent launches share the same marker; it disappears when
    /// the last launcher process exits, even after a crash.
    /// </summary>
    public static IDisposable EnterGameSession() => EnterGameSession(SessionMarkerName);

    internal static IDisposable EnterGameSession(string markerName)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, markerName);
        }
        catch
        {
            // Without the marker the sync simply keeps running during this
            // game; the launch itself must never fail because of it.
            return EmptyScope.Instance;
        }
    }

    public static bool IsGameActive() =>
        IsSteamGameRunning() || IsGameSessionActive(SessionMarkerName);

    internal static bool IsGameSessionActive(string markerName)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(markerName, out var marker))
            {
                marker.Dispose();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsSteamGameRunning()
    {
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return steamKey?.GetValue("RunningAppID") is int runningAppId && runningAppId != 0;
        }
        catch
        {
            // An unreadable value must not pause the library sync forever.
            return false;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}
