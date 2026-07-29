using System.Reflection;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record XboxDirectInstallCapabilityState(
    bool Supported,
    string Reason,
    DateTimeOffset CheckedAtUtc,
    string OsVersion,
    string AppVersion);

/// <summary>
/// Remembers structural direct-install incompatibilities without turning a
/// transient Store failure into a permanent machine-wide decision.
/// </summary>
internal static class XboxDirectInstallCapabilityStore
{
    private const string MutexName = @"Local\ToolsForSteamXboxInstallCapability";
    private static readonly TimeSpan UnsupportedRetryInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary-xbox-install-capability.json");

    public static bool ShouldAttemptDirectInstall(out string reason)
    {
        reason = string.Empty;
        var state = Read();
        if (state is null || state.Supported)
        {
            return true;
        }

        if (!string.Equals(state.OsVersion, GetOsVersion(), StringComparison.Ordinal) ||
            !string.Equals(state.AppVersion, GetAppVersion(), StringComparison.Ordinal) ||
            DateTimeOffset.UtcNow - state.CheckedAtUtc >= UnsupportedRetryInterval)
        {
            return true;
        }

        reason = string.IsNullOrWhiteSpace(state.Reason)
            ? "Direct Xbox installation is not available on this PC."
            : state.Reason;
        return false;
    }

    public static void MarkSupported()
    {
        Write(new XboxDirectInstallCapabilityState(
            true,
            string.Empty,
            DateTimeOffset.UtcNow,
            GetOsVersion(),
            GetAppVersion()));
    }

    public static void MarkUnsupported(string reason)
    {
        Write(new XboxDirectInstallCapabilityState(
            false,
            reason.Trim(),
            DateTimeOffset.UtcNow,
            GetOsVersion(),
            GetAppVersion()));
    }

    private static XboxDirectInstallCapabilityState? Read()
    {
        XboxDirectInstallCapabilityState? result = null;
        WithLock(() =>
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    result = JsonSerializer.Deserialize<XboxDirectInstallCapabilityState>(
                        File.ReadAllText(FilePath),
                        JsonOptions);
                }
            }
            catch
            {
                result = null;
            }
        });
        return result;
    }

    private static void Write(XboxDirectInstallCapabilityState state)
    {
        WithLock(() =>
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $"omnilibrary-xbox-install-capability-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        });
    }

    private static void WithLock(Action action)
    {
        using var mutex = new Mutex(false, MutexName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                return;
            }

            action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string GetOsVersion()
    {
        return Environment.OSVersion.VersionString;
    }

    private static string GetAppVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ??
               typeof(XboxDirectInstallCapabilityStore).Assembly.GetName().Version?.ToString() ??
               "unknown";
    }
}
