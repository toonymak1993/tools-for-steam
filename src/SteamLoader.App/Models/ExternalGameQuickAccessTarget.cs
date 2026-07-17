namespace SteamLoader.App.Models;

public sealed record ExternalGameQuickAccessTarget(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string ExecutablePath,
    string WindowHandle,
    bool? OverlayRendererMissing);
