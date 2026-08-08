using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using SteamLoader.App.Infrastructure.Helpers;

namespace SteamLoader.App.Infrastructure.Themes;

/// <summary>
/// Windows only exposes the lock screen image through the machine-wide
/// PersonalizationCSP policy keys, so applying or clearing it requires one
/// short elevated relaunch of the current executable, mirroring the GOG
/// elevated worker pattern used elsewhere in TFS.
/// </summary>
internal static class LockScreenWallpaperElevatedWorker
{
    public const string ElevatedArgument = "--set-lock-screen-wallpaper-elevated";
    private const string ApplyMode = "apply";
    private const string ClearMode = "clear";
    private const string PersonalizationCspKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

    public static bool RequestApply(string imagePath) => RequestElevated(ApplyMode, imagePath);

    public static bool RequestClear() => RequestElevated(ClearMode, string.Empty);

    public static int RunElevated(string mode, string imagePath, string resultToken)
    {
        var success = false;
        var error = string.Empty;
        try
        {
            if (!ElevatedHelperTaskService.IsCurrentProcessElevated())
            {
                error = "The lock screen wallpaper worker did not receive administrator rights.";
                return 1;
            }

            using var key = Registry.LocalMachine.CreateSubKey(PersonalizationCspKeyPath, writable: true);
            if (key is null)
            {
                error = "The lock screen policy key could not be opened.";
                return 1;
            }

            if (string.Equals(mode, ApplyMode, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    error = "The wallpaper image is missing.";
                    return 1;
                }

                key.SetValue("LockScreenImagePath", imagePath, RegistryValueKind.String);
                key.SetValue("LockScreenImageUrl", imagePath, RegistryValueKind.String);
                key.SetValue("LockScreenImageStatus", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue("LockScreenImagePath", throwOnMissingValue: false);
                key.DeleteValue("LockScreenImageUrl", throwOnMissingValue: false);
                key.DeleteValue("LockScreenImageStatus", throwOnMissingValue: false);
            }

            success = true;
            return 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return 1;
        }
        finally
        {
            WriteElevatedResult(resultToken, success, error);
        }
    }

    private static bool RequestElevated(string mode, string imagePath)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var resultToken = Guid.NewGuid().ToString("N");
        var resultPath = GetElevatedResultPath(resultToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }

            var arguments = string.Join(
                ' ',
                ElevatedArgument,
                mode,
                QuoteArgument(imagePath),
                resultToken);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            process?.WaitForExit();

            if (process is { ExitCode: 0 } && File.Exists(resultPath))
            {
                var result = JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(resultPath));
                return result?.Success == true;
            }

            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // The user declined the UAC prompt; leave the lock screen untouched.
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch
            {
            }
        }
    }

    private static string QuoteArgument(string value)
    {
        return string.IsNullOrEmpty(value) ? "\"\"" : $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static string GetElevatedResultPath(string resultToken)
    {
        return Path.Combine(Path.GetTempPath(), "ToolsForSteam", "theme-results", $"{resultToken}.json");
    }

    private static void WriteElevatedResult(string resultToken, bool success, string error)
    {
        try
        {
            var resultPath = GetElevatedResultPath(resultToken);
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new ElevatedResult(success, error)));
        }
        catch
        {
        }
    }

    private sealed record ElevatedResult(bool Success, string Error);
}
