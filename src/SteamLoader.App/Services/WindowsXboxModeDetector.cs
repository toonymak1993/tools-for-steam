using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SteamLoader.App.Services;

/// <summary>
/// Reads whether Windows' own Xbox Mode (Gaming Full Screen Experience) is
/// turned on. It starts its own home app at sign-in and owns the screen, so it
/// cannot work together with the TFS Console Mode. TFS only
/// reports it; it never changes the user's Xbox Mode setting.
/// </summary>
internal sealed class WindowsXboxModeDetector
{
    private const string DefaultGamingConfigurationKeyPath = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string StartupToGamingHomeValueName = "StartupToGamingHome";
    private const string GamingExperienceLibrary = "api-ms-win-gaming-experience-l1-1-0.dll";

    private static readonly Lazy<IsGamingFullScreenExperienceActiveDelegate?> IsActiveExport =
        new(LoadIsActiveExport, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _gamingConfigurationKeyPath;
    private readonly Func<bool> _isSessionActive;

    public WindowsXboxModeDetector()
        : this(DefaultGamingConfigurationKeyPath, IsFullScreenExperienceActive)
    {
    }

    internal WindowsXboxModeDetector(string gamingConfigurationKeyPath, Func<bool> isSessionActive)
    {
        _gamingConfigurationKeyPath = gamingConfigurationKeyPath;
        _isSessionActive = isSessionActive;
    }

    /// <summary>
    /// True when Windows starts Xbox Mode at sign-in or an Xbox Mode session
    /// is running right now.
    /// </summary>
    public bool IsEnabled() => StartsAtSignIn() || IsSessionActive();

    public bool StartsAtSignIn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_gamingConfigurationKeyPath, writable: false);
            return key?.GetValue(StartupToGamingHomeValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public bool IsSessionActive()
    {
        try
        {
            return _isSessionActive();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFullScreenExperienceActive()
    {
        return IsActiveExport.Value?.Invoke() == true;
    }

    private static IsGamingFullScreenExperienceActiveDelegate? LoadIsActiveExport()
    {
        // Older Windows builds have no Xbox Mode; the library or export is
        // simply missing there. The library stays loaded for the process.
        if (!NativeLibrary.TryLoad(GamingExperienceLibrary, out var library) ||
            !NativeLibrary.TryGetExport(library, "IsGamingFullScreenExperienceActive", out var export))
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer<IsGamingFullScreenExperienceActiveDelegate>(export);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool IsGamingFullScreenExperienceActiveDelegate();
}
