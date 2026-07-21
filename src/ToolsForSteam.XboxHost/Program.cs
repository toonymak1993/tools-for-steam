using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using ToolsForSteam.Splash;

namespace ToolsForSteam.XboxHost;

internal static class Program
{
    private const string StateKeyPath = @"Software\GCM\SteamTools";
    private const string InstallPathValueName = "InstallPath";
    private const string MainExecutableName = "ToolsForSteam.exe";
    private const string RequestSteamAttentionArgument = "--request-steam-attention";
    private const string RepairSteamStartupArgument = "--repair-steam-startup";
    private static readonly TimeSpan MinimumSplashDuration = TimeSpan.FromSeconds(10);
    private static string? _logPath;

    [STAThread]
    private static int Main()
    {
        using var instanceMutex = new Mutex(true, @"Local\ToolsForSteam.XboxHost", out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        try
        {
            var executablePath = ResolveMainExecutablePath();
            _logPath = Path.Combine(Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory, "data", "xbox-host-startup.log");
            Log($"Xbox host started session={Process.GetCurrentProcess().SessionId} executable={executablePath}");
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                Log("ToolsForSteam.exe was not found");
                return 2;
            }

            var splashSettings = LoadSplashSettings(executablePath);
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            using var splashWindow = CreateSplashWindow(executablePath, splashSettings);
            splashWindow.Show();
            splashWindow.Activate();
            System.Windows.Forms.Application.DoEvents();
            Log("packaged Xbox host splash window is visible");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--tray --xbox-bootstrap --xbox-hosted-splash --startup-sync",
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = true
            });

            if (process is null)
            {
                Log("ToolsForSteam.exe could not be started");
                return 3;
            }

            Log($"ToolsForSteam.exe launched pid={process.Id}");
            if (!WaitForSteamWindow(TimeSpan.FromSeconds(220), splashWindow, executablePath))
            {
                Log("Steam remained invisible after attention and hard-repair attempts");
            }

            WaitForXboxModeToClose(process);
            Log("Xbox mode session closed");
            return 0;
        }
        catch (Exception exception)
        {
            Log($"Xbox host failed: {exception}");
            return 1;
        }
    }

    private static void WaitForXboxModeToClose(Process toolsForSteamProcess)
    {
        try
        {
            while (IsGamingFullScreenExperienceActive())
            {
                Thread.Sleep(1000);
            }
        }
        catch (DllNotFoundException)
        {
            toolsForSteamProcess.WaitForExit();
        }
        catch (EntryPointNotFoundException)
        {
            toolsForSteamProcess.WaitForExit();
        }
    }

    private static string ResolveMainExecutablePath()
    {
        using var stateKey = Registry.CurrentUser.OpenSubKey(StateKeyPath, writable: false);
        var installPath = stateKey?.GetValue(InstallPathValueName) as string;
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            return Path.Combine(installPath.Trim().Trim('"'), MainExecutableName);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "ToolsForSteam",
            MainExecutableName);
    }

    private static bool WaitForSteamWindow(TimeSpan timeout, Form? splashWindow, string executablePath)
    {
        var splashStartedAt = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + timeout;
        var nextRecoveryAt = splashStartedAt + TimeSpan.FromSeconds(20);
        var recoveryAttempt = 0;
        var nextHardRepairAt = splashStartedAt + TimeSpan.FromSeconds(70);
        var hardRepairAttempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            System.Windows.Forms.Application.DoEvents();
            var minimumSplashElapsed =
                splashWindow is null || DateTime.UtcNow - splashStartedAt >= MinimumSplashDuration;
            if (HasVisibleSteamWindow() && minimumSplashElapsed)
            {
                CloseSplashWindow(splashWindow);
                Thread.Sleep(1500);
                Log("Steam window became visible");
                return true;
            }

            if (DateTime.UtcNow >= nextRecoveryAt)
            {
                recoveryAttempt += 1;
                Log($"Steam is still not visible; requesting UI recovery attempt={recoveryAttempt}");
                RequestSteamAttention(executablePath);
                nextRecoveryAt = splashStartedAt + TimeSpan.FromSeconds(20 + (recoveryAttempt * 25));
            }

            if (DateTime.UtcNow >= nextHardRepairAt && hardRepairAttempts < 2)
            {
                hardRepairAttempts += 1;
                if (IsSteamDevToolsReady())
                {
                    Log("Steam DevTools responds while the UI is invisible; the TFS UI watchdog owns recovery");
                }
                else
                {
                    Log($"Steam and DevTools are unresponsive; requesting hard startup repair attempt={hardRepairAttempts}");
                    RequestSteamStartupRepair(executablePath);
                }
                nextHardRepairAt = splashStartedAt + TimeSpan.FromSeconds(70 + (hardRepairAttempts * 75));
            }

            Thread.Sleep(500);
        }

        CloseSplashWindow(splashWindow);
        return false;
    }

    private static void CloseSplashWindow(Form? splashWindow)
    {
        if (splashWindow is null || splashWindow.IsDisposed)
        {
            return;
        }

        splashWindow.Close();
        System.Windows.Forms.Application.DoEvents();
        Log("packaged Xbox host splash window closed");
    }

    private static Form CreateSplashWindow(string executablePath, XboxSplashSettings settings)
    {
        var installDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        var customImagePath =
            settings.ArtworkMode == StartupSplashArtworkMode.Custom && File.Exists(settings.CustomImagePath)
                ? settings.CustomImagePath
                : string.Empty;
        var splashView = new StartupSplashView
        {
            CustomImagePath = customImagePath,
            DetailText = "Starting the background service and preparing the fast Steam hand-off.",
            StateText = "Steam is loading behind this screen."
        };

        var window = new Form
        {
            Text = "Tools for Steam",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080),
            BackColor = Color.Black,
            TopMost = true,
            ShowInTaskbar = false,
            KeyPreview = false
        };
        var elementHost = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = splashView
        };
        window.Controls.Add(elementHost);
        if (string.IsNullOrWhiteSpace(customImagePath))
        {
            _ = PopulateSplashCoversAsync(splashView, ResolveSteamRoot(executablePath));
        }

        Log($"created packaged splash settings={settings} installDirectory={installDirectory}");
        return window;
    }

    private static async Task PopulateSplashCoversAsync(StartupSplashView splashView, string? steamRoot)
    {
        try
        {
            var covers = await StartupSplashCoverService.LoadAsync(steamRoot).ConfigureAwait(false);
            await splashView.Dispatcher.InvokeAsync(() => splashView.GameCovers = covers);
            Log($"packaged splash loaded game covers count={covers.Count} steamRoot={steamRoot ?? "<missing>"}");
        }
        catch (Exception exception)
        {
            Log($"packaged splash cover loading failed: {exception.Message}");
        }
    }

    private static string? ResolveSteamRoot(string executablePath)
    {
        var settingsPath = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "data", "steam-install-path.json");
        try
        {
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("manualOverridePath", out var pathProperty) &&
                    pathProperty.ValueKind == JsonValueKind.String)
                {
                    var configuredPath = NormalizeSteamRoot(pathProperty.GetString());
                    if (configuredPath is not null)
                    {
                        return configuredPath;
                    }
                }
            }
        }
        catch
        {
        }

        using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: false);
        foreach (var valueName in new[] { "SteamPath", "InstallPath" })
        {
            var registryPath = NormalizeSteamRoot(steamKey?.GetValue(valueName) as string);
            if (registryPath is not null)
            {
                return registryPath;
            }
        }

        return NormalizeSteamRoot(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam"));
    }

    private static string? NormalizeSteamRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = Path.GetFullPath(path.Trim().Trim('"'));
        if (normalized.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetDirectoryName(normalized) ?? normalized;
        }

        return File.Exists(Path.Combine(normalized, "steam.exe")) ||
            Directory.Exists(Path.Combine(normalized, "userdata"))
                ? normalized
                : null;
    }

    private static XboxSplashSettings LoadSplashSettings(string executablePath)
    {
        var settingsPath = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "data", "tfs.json");
        if (!File.Exists(settingsPath))
        {
            return XboxSplashSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("splashScreen", out var splash) ||
                splash.ValueKind != JsonValueKind.Object)
            {
                return XboxSplashSettings.Default;
            }

            var customImagePath = GetString(splash, "customImagePath");
            if (string.IsNullOrWhiteSpace(customImagePath))
            {
                customImagePath = GetString(splash, "wallpaperPath");
            }

            return new XboxSplashSettings(
                StartupSplashArtworkMode.Normalize(GetString(splash, "artworkMode"), customImagePath),
                customImagePath);
        }
        catch (Exception exception)
        {
            Log($"splash settings could not be read; using defaults: {exception.Message}");
            return XboxSplashSettings.Default;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void RequestSteamAttention(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = RequestSteamAttentionArgument,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = true
            });
            Log($"Steam attention request launched pid={process?.Id.ToString() ?? "<missing>"}");
        }
        catch (Exception exception)
        {
            Log($"Steam attention request failed: {exception}");
        }
    }

    private static void RequestSteamStartupRepair(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = RepairSteamStartupArgument,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = true
            });
            Log($"hard Steam startup repair launched pid={process?.Id.ToString() ?? "<missing>"}");
        }
        catch (Exception exception)
        {
            Log($"hard Steam startup repair failed: {exception}");
        }
    }

    private static bool IsSteamDevToolsReady()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = client.GetAsync("http://127.0.0.1:8080/json/list").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var path = _logPath ?? Path.Combine(Path.GetTempPath(), "ToolsForSteam-XboxHost.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static bool HasVisibleSteamWindow()
    {
        foreach (var processName in new[] { "steamwebhelper", "steam" })
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Any(process =>
                {
                    try
                    {
                        return !process.HasExited && process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch
                    {
                        return false;
                    }
                }))
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsGamingFullScreenExperienceActive();

    private sealed record XboxSplashSettings(string ArtworkMode, string CustomImagePath)
    {
        public static XboxSplashSettings Default { get; } = new(StartupSplashArtworkMode.Dynamic, string.Empty);
    }
}
