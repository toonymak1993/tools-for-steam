using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Services;

public enum SteamUiState
{
    Unknown,
    Starting,
    Updating,
    Login,
    Offline,
    Error,
    Desktop,
    Gamepad
}

internal static class SteamUiStateClassifier
{
    public static SteamUiState Classify(IReadOnlyList<SteamDevToolsTarget> targets)
    {
        if (targets.Count == 0)
        {
            return SteamUiState.Starting;
        }

        var combined = string.Join(' ', targets.Select(target => target.Title + " " + target.Url));
        if (ContainsAny(combined, "selfupdate", "self-update", "bootstrap/update", "client-update"))
        {
            return SteamUiState.Updating;
        }

        if (targets.Any(target => ContainsAny(
                target.Title + " " + target.Url,
                "Big-Picture",
                "browserType=3",
                "Valve%20Steam%20Gamepad",
                "Valve Steam Gamepad",
                "QuickAccess",
                "MainMenu")))
        {
            return SteamUiState.Gamepad;
        }

        if (ContainsAny(combined, "login", "logon", "signin", "sign-in"))
        {
            return SteamUiState.Login;
        }

        if (ContainsAny(combined, "offline", "no_connection", "noconnection"))
        {
            return SteamUiState.Offline;
        }

        if (ContainsAny(combined, "error", "fatal", "crash"))
        {
            return SteamUiState.Error;
        }

        return targets.Any(target => target.Type.Equals("page", StringComparison.OrdinalIgnoreCase))
            ? SteamUiState.Desktop
            : SteamUiState.Unknown;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
