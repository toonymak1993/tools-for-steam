using SteamLoader.App.Infrastructure.Performance;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Hosting;
using SteamLoader.App.Models;
using SteamLoader.App.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace SteamLoader.App.Infrastructure.Helpers;

public sealed class TfsGamepadHelperHost
{
    private readonly object _logLock = new();
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "data", "gamepad-helper-runtime.log");

    public async Task<int> RunAsync()
    {
        Log("helper-entry");
        using var mutex = new Mutex(true, SteamLoaderRuntime.GamepadHelperMutexName, out var createdNew);
        if (!createdNew)
        {
            Log("helper-exit reason=duplicate-instance");
            return 0;
        }

        Log(
            $"helper-start pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId} " +
            $"elevated={ElevatedHelperTaskService.IsCurrentProcessElevated()} user={WindowsIdentity.GetCurrent().Name} " +
            $"expectedHidUsage={HidMenuButtonMonitor.ExpectedMenuButtonUsage}");

        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;

        EventHandler processExitHandler = (_, _) =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }
        };
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        try
        {
            using var hidMenuButtonMonitor = new HidMenuButtonMonitor();
            using var controllerApiClient = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1:47652/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
            controllerApiClient.DefaultRequestHeaders.Add(
                LocalApiSession.HeaderName,
                LocalApiSession.GetOrCreateDefault());
            hidMenuButtonMonitor.ReportObserved += report =>
            {
                var usages = report.ButtonUsages.Count == 0 ? "-" : string.Join(",", report.ButtonUsages);
                Log(
                    $"hid-report device=0x{unchecked((ulong)report.DeviceHandle.ToInt64()):X} " +
                    $"reportLength={report.ReportLength} usages=[{usages}] expectedUsageDown={report.IsExpectedMenuUsagePressed}");
            };

            Log("hid-monitor-ready");
            var controllerShortcutService = new ControllerShortcutService(
                isEnabled: () => true,
                isBigPictureForeground: SteamBigPictureForegroundDetector.IsBigPictureForeground,
                isGameInForeground: () =>
                    !SteamBigPictureForegroundDetector.IsBigPictureForeground() &&
                    PerformanceForegroundTargetResolver.TryResolve() is not null,
                isHidMenuButtonDown: () => hidMenuButtonMonitor.IsMenuDown,
                openSteamMenuAsync: () => TryOpenSteamPanelAsync(controllerApiClient, "api/control/steam-menu"),
                openQuickAccessMenuAsync: () => TryOpenSteamPanelAsync(controllerApiClient, "api/control/quick-access"),
                sendControlDigitAsync: digit => ControllerShortcutService.SendControlDigitKeyboardAsync(digit, Log),
                diagnosticLog: Log,
                isHidBackButtonDown: () => hidMenuButtonMonitor.IsBackDown,
                tryOpenExternalGameQuickAccessAsync: () => TryOpenExternalGameQuickAccessAsync(controllerApiClient));

            var hardwareCommandProcessor = new HandheldHardwareCommandProcessor(
                Path.Combine(AppContext.BaseDirectory, "data"),
                Log);
            await using var replacementRuntime = new HandheldReplacementRuntime(
                Path.Combine(AppContext.BaseDirectory, "data"),
                Log);
            await Task.WhenAll(
                    controllerShortcutService.RunAsync(cancellation.Token),
                    hardwareCommandProcessor.RunAsync(cancellation.Token),
                    replacementRuntime.RunAsync(cancellation.Token))
                .ConfigureAwait(false);
            Log("helper-exit reason=cancelled");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Log($"helper-fatal type={exception.GetType().Name} message={exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private async Task<bool> TryOpenSteamPanelAsync(HttpClient client, string path)
    {
        try
        {
            using var response = await client.PostAsync(path, content: null);
            Log($"steam-panel-api path={path} status={(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception)
        {
            Log($"steam-panel-api path={path} failed={exception.GetType().Name}:{exception.Message}");
            return false;
        }
    }

    private async Task<bool> TryOpenExternalGameQuickAccessAsync(HttpClient client)
    {
        const string path = "api/control/external-game-quick-access";
        try
        {
            var foregroundTarget = PerformanceForegroundTargetResolver.TryResolve();
            ExternalGameQuickAccessTarget? payload = null;
            if (foregroundTarget is not null)
            {
                payload = new ExternalGameQuickAccessTarget(
                    foregroundTarget.ProcessId,
                    foregroundTarget.ProcessName,
                    foregroundTarget.WindowTitle,
                    foregroundTarget.ExecutablePath,
                    foregroundTarget.WindowHandle,
                    XboxStoreLaunchHost.IsSteamOverlayRendererMissing((uint)foregroundTarget.ProcessId));
            }

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(path, content);
            Log(
                $"steam-panel-api path={path} status={(int)response.StatusCode} " +
                $"pid={payload?.ProcessId ?? 0} name={payload?.ProcessName ?? "-"} " +
                $"overlayMissing={payload?.OverlayRendererMissing?.ToString() ?? "unknown"}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception)
        {
            Log($"steam-panel-api path={path} failed={exception.GetType().Name}:{exception.Message}");
            return false;
        }
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(
                    _logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
