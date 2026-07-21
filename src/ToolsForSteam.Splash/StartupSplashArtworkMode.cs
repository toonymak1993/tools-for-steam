namespace ToolsForSteam.Splash;

public static class StartupSplashArtworkMode
{
    public const string Dynamic = "dynamic";
    public const string Custom = "custom";

    public static string Normalize(string? mode, string? configuredCustomImagePath = null)
    {
        if (string.Equals(mode, Custom, StringComparison.OrdinalIgnoreCase))
        {
            return Custom;
        }

        if (string.Equals(mode, Dynamic, StringComparison.OrdinalIgnoreCase))
        {
            return Dynamic;
        }

        // Older builds only stored wallpaperPath. Treat it as an intentional
        // custom selection so existing users keep the artwork they chose.
        return string.IsNullOrWhiteSpace(configuredCustomImagePath) ? Dynamic : Custom;
    }
}
