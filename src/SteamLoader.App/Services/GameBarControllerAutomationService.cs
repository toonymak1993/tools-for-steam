using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace SteamLoader.App.Services;

/// <summary>
/// Game Bar controller shortcut automation (like Playhub's Gaming Mode): the
/// Xbox button must not open Game Bar on top of Steam Big Picture, but Xbox /
/// Microsoft Store games often rely on it. While Big Picture is open the
/// Windows setting "Open Game Bar using this button on a controller" is turned
/// off; while an Xbox / Microsoft Store game runs it is turned on. Outside
/// Big Picture, and when the automation is switched off or TFS exits, the
/// user's own value comes back. The original value is kept on disk so a crash
/// never leaves Windows changed.
/// </summary>
public sealed class GameBarControllerAutomationService : IDisposable
{
    internal const string RegistryPath = @"Software\Microsoft\GameBar";
    internal const string ValueName = "UseNexusForGameBarEnabled";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1500);

    // Packages under WindowsApps that are apps, not games.
    private static readonly string[] NonGamePackagePrefixes =
    [
        "Microsoft.GamingApp",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxApp",
        "Microsoft.WindowsTerminal",
        "Microsoft.Windows",
        "MicrosoftWindows.",
        "Microsoft.MicrosoftEdge",
        "Microsoft.Office",
        "Microsoft.WindowsStore",
        "Microsoft.Paint",
        "Microsoft.ScreenSketch",
        "SpotifyAB.",
        "5319275A.WhatsApp",
    ];

    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isBigPictureOpen;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private int _gamePid;
    private bool _disposed;

    public GameBarControllerAutomationService(
        Func<bool> isEnabled,
        Func<bool> isBigPictureOpen,
        string statePath,
        Action<string>? log = null)
    {
        _isEnabled = isEnabled;
        _isBigPictureOpen = isBigPictureOpen;
        _statePath = statePath;
        _log = log;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), Interval);
    }

    /// <summary>Desired Windows value: true = on, false = off, null = the user's own value.</summary>
    internal static bool? Decide(bool automationEnabled, bool xboxGameRunning, bool bigPictureOpen)
    {
        if (!automationEnabled)
        {
            return null;
        }

        if (xboxGameRunning)
        {
            return true;
        }

        return bigPictureOpen ? false : null;
    }

    /// <summary>
    /// Xbox / Microsoft Store games live in XboxGames or ModifiableWindowsApps.
    /// WindowsApps also holds every ordinary MSIX app (Claude, WhatsApp, ...),
    /// so a package there only counts as a game when it ships the
    /// MicrosoftGame.config that every Xbox PC (GDK) game has.
    /// </summary>
    internal static bool IsXboxStoreGamePath(string? path, Func<string, bool>? isGamePackage = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\ModifiableWindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var marker = @"\WindowsApps\";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var package = normalized[(index + marker.Length)..].Split('\\')[0];
        if (package.Length == 0 ||
            NonGamePackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var packageRoot = normalized[..(index + marker.Length)] + package;
        return (isGamePackage ?? HasMicrosoftGameConfig)(packageRoot);
    }

    private static bool HasMicrosoftGameConfig(string packageRoot)
    {
        try
        {
            return File.Exists(Path.Combine(packageRoot, "MicrosoftGame.config"));
        }
        catch
        {
            return false;
        }
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var enabled = _isEnabled();
            var gameRunning = enabled && IsXboxGameRunning();
            var desired = Decide(enabled, gameRunning, enabled && _isBigPictureOpen());
            Apply(desired);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"tick failed: {exception.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private bool IsXboxGameRunning()
    {
        if (_gamePid != 0 && IsAlive(_gamePid))
        {
            return true;
        }

        _gamePid = 0;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
        {
            return false;
        }

        var path = TryGetProcessPath((int)pid);
        if (!IsXboxStoreGamePath(path))
        {
            return false;
        }

        _gamePid = (int)pid;
        _log?.Invoke($"xbox game in front: pid={pid} path={path}");
        return true;
    }

    private void Apply(bool? desired)
    {
        var saved = LoadState();
        if (desired is null)
        {
            if (saved is not null)
            {
                Write(saved.OriginalValue);
                File.Delete(_statePath);
                _log?.Invoke($"restored original value {saved.OriginalValue?.ToString() ?? "(unset)"}");
            }

            return;
        }

        if (saved is null)
        {
            // Remember the user's own value before the first change.
            SaveState(new SavedState(Read()));
        }

        var wanted = desired.Value ? 1 : 0;
        if (Read() != wanted)
        {
            Write(wanted);
            _log?.Invoke($"set {ValueName}={wanted}");
        }
    }

    private static int? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    private static void Write(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (value is null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(ValueName, value.Value, RegistryValueKind.DWord);
        }
    }

    private SavedState? LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_statePath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveState(SavedState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? TryGetProcessPath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Gives the user's value back (automation off, or TFS exits).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            try
            {
                Apply(null);
            }
            catch (Exception exception)
            {
                _log?.Invoke($"restore on exit failed: {exception.Message}");
            }
        }
    }

    private sealed record SavedState(int? OriginalValue);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
