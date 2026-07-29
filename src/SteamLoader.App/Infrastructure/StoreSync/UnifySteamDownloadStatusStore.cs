using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record UnifySteamDownloadStatus(
    string Status,
    int ProgressPercent,
    string DetailText,
    DateTimeOffset UpdatedAtUtc,
    int WorkerProcessId = 0,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    double DownloadBytesPerSecond = 0,
    double DecompressedBytesPerSecond = 0,
    double DiskWriteBytesPerSecond = 0,
    double DiskReadBytesPerSecond = 0,
    int Attempt = 1,
    string GameTitle = "",
    uint SteamAppId = 0,
    DateTimeOffset? WorkerStartedAtUtc = null);

internal sealed record UnifySteamInterruptedDownload(
    string StoreId,
    string GameId,
    UnifySteamDownloadStatus Status);

/// <summary>
/// A tiny cross-process status bridge between the hidden download worker and
/// Steam's UI process.
/// </summary>
internal static class UnifySteamDownloadStatusStore
{
    private const string MutexName = @"Local\ToolsForSteamOmniLibraryDownloads";
    private static readonly TimeSpan ProgressPersistenceInterval =
        TimeSpan.FromMilliseconds(900);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary-downloads.json");

    private static string BackupFilePath => $"{FilePath}.bak";

    public static UnifySteamDownloadStatus Get(string storeId, string gameId)
    {
        return Get(GetAll(), storeId, gameId);
    }

    public static IReadOnlyDictionary<string, UnifySteamDownloadStatus> GetAll()
    {
        var entries = new Dictionary<string, UnifySteamDownloadStatus>(StringComparer.OrdinalIgnoreCase);
        WithLock(() =>
        {
            entries = ReadAllUnlocked();
            var changed = false;
            foreach (var key in entries.Keys.ToArray())
            {
                var status = entries[key];
                var normalized = Normalize(key, status);
                if (!Equals(status, normalized))
                {
                    entries[key] = normalized;
                    changed = true;
                }

                var age = DateTimeOffset.UtcNow - entries[key].UpdatedAtUtc;
                if ((entries[key].Status == "completed" && age > TimeSpan.FromHours(1)) ||
                    (entries[key].Status == "action-required" &&
                     age > TimeSpan.FromHours(6)) ||
                    (entries[key].Status == "uninstall-action-required" &&
                     age > TimeSpan.FromHours(24)) ||
                    (entries[key].Status is "canceled" && age > TimeSpan.FromHours(24)) ||
                    (entries[key].Status is "failed" or "cancel-failed" or "uninstall-failed" &&
                     age > TimeSpan.FromDays(7)))
                {
                    entries.Remove(key);
                    changed = true;
                }
            }

            if (changed)
            {
                WriteAllUnlocked(entries);
            }
        });

        return entries;
    }

    public static UnifySteamDownloadStatus Get(
        IReadOnlyDictionary<string, UnifySteamDownloadStatus> entries,
        string storeId,
        string gameId)
    {
        if (!entries.TryGetValue(BuildKey(storeId, gameId), out var status))
        {
            return new UnifySteamDownloadStatus("idle", 0, string.Empty, DateTimeOffset.MinValue);
        }

        return status;
    }

    public static long GetRevision()
    {
        try
        {
            var file = new FileInfo(FilePath);
            return file.Exists
                ? HashCode.Combine(file.LastWriteTimeUtc.Ticks, file.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static IReadOnlyList<UnifySteamInterruptedDownload> GetRecoverableDownloads(
        string storeId)
    {
        var normalizedStoreId = storeId.Trim().ToLowerInvariant();
        var prefix = $"{normalizedStoreId}:";
        var downloads = new List<UnifySteamInterruptedDownload>();
        WithLock(() =>
        {
            foreach (var (key, status) in ReadAllUnlocked())
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !IsActivelyTransferring(status.Status) ||
                    status.WorkerProcessId > 0 && IsProcessRunning(
                        status.WorkerProcessId,
                        status.UpdatedAtUtc,
                        status.WorkerStartedAtUtc))
                {
                    continue;
                }

                var gameId = key[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    downloads.Add(new UnifySteamInterruptedDownload(
                        normalizedStoreId,
                        gameId,
                        status));
                }
            }
        });
        return downloads;
    }

    public static IReadOnlyList<UnifySteamInterruptedDownload> GetRecoverableCancellations()
    {
        var cancellations = new List<UnifySteamInterruptedDownload>();
        WithLock(() =>
        {
            foreach (var (key, status) in ReadAllUnlocked())
            {
                if (!status.Status.Equals(
                        "canceling",
                        StringComparison.OrdinalIgnoreCase) ||
                    status.WorkerProcessId > 0 &&
                    IsProcessRunning(
                        status.WorkerProcessId,
                        status.UpdatedAtUtc,
                        status.WorkerStartedAtUtc))
                {
                    continue;
                }

                var separator = key.IndexOf(':');
                if (separator <= 0 || separator >= key.Length - 1)
                {
                    continue;
                }

                cancellations.Add(new UnifySteamInterruptedDownload(
                    key[..separator],
                    key[(separator + 1)..],
                    status));
            }
        });
        return cancellations;
    }

    private static UnifySteamDownloadStatus Normalize(
        string key,
        UnifySteamDownloadStatus status)
    {
        var age = DateTimeOffset.UtcNow - status.UpdatedAtUtc;
        var storeOwnsTransferLifecycle = key.StartsWith(
            "xbox-game-pass:",
            StringComparison.OrdinalIgnoreCase);
        var activeWorkerMissing =
                                  IsBusyOperation(status.Status) &&
                                  status.WorkerProcessId > 0 &&
                                  !IsProcessRunning(
                                      status.WorkerProcessId,
                                      status.UpdatedAtUtc,
                                      status.WorkerStartedAtUtc);
        // A Windows Store download can briefly replace its acquisition worker
        // while it hands the package to deployment. Keep the last real progress
        // visible during that window; the Xbox install scan will still switch
        // the game to Play as soon as the files are complete.
        var missingWorkerExpired =
            !storeOwnsTransferLifecycle &&
            activeWorkerMissing &&
            age > TimeSpan.FromMinutes(2);
        var operationExpired =
            IsBusyOperation(status.Status) &&
            age > TimeSpan.FromHours(24);
        return missingWorkerExpired || operationExpired
            ? status.Status == "uninstalling"
                ? status with
                {
                    Status = "uninstall-failed",
                    DetailText = "The previous uninstall stopped. Select Retry Uninstall to try again.",
                }
                : status.Status == "canceling"
                    ? status with
                    {
                        Status = "cancel-failed",
                        DetailText =
                            "Download cleanup stopped before completion. Select Delete Partial Files to retry.",
                    }
                : status with
            {
                Status = "failed",
                DetailText = "The previous download worker stopped. Select Retry Download to continue from its saved files.",
            }
            : status;
    }

    public static bool IsActivelyTransferring(string? status)
    {
        return status?.Trim().ToLowerInvariant() is
            "preparing" or
            "queued" or
            "downloading" or
            "reconnecting" or
            "finalizing";
    }

    public static bool IsDownloadCenterStatus(string? status)
    {
        return IsActivelyTransferring(status) ||
               status?.Trim().ToLowerInvariant() is
                   "paused" or
                   "failed" or
                   "canceled" or
                   "canceling" or
                   "cancel-failed" or
                   "action-required" or
                   "uninstalling" or
                   "uninstall-action-required" or
                   "uninstall-failed" or
                   "completed";
    }

    public static bool IsBusyOperation(string? status)
    {
        return IsActivelyTransferring(status) ||
               status?.Trim().ToLowerInvariant() is
                   "canceling" or
                   "uninstalling";
    }

    public static bool BlocksStoreDisconnect(string? status)
    {
        return IsBusyOperation(status) ||
               status?.Trim().ToLowerInvariant() is
                   "paused" or
                   "action-required" or
                   "uninstall-action-required";
    }

    public static void Update(
        string storeId,
        string gameId,
        string status,
        int progressPercent,
        string detailText,
        int? workerProcessId = null,
        long downloadedBytes = 0,
        long totalBytes = 0,
        double downloadBytesPerSecond = 0,
        double decompressedBytesPerSecond = 0,
        double diskWriteBytesPerSecond = 0,
        double diskReadBytesPerSecond = 0,
        int attempt = 1,
        string? gameTitle = null,
        uint steamAppId = 0)
    {
        WithLock(() =>
        {
            var entries = ReadAllUnlocked();
            var key = BuildKey(storeId, gameId);
            entries.TryGetValue(key, out var current);
            var normalizedStatus = status.Trim().ToLowerInvariant();
            var effectiveWorkerProcessId = workerProcessId ?? Environment.ProcessId;
            var effectiveGameTitle = string.IsNullOrWhiteSpace(gameTitle)
                ? current?.GameTitle ?? string.Empty
                : gameTitle.Trim();
            var effectiveSteamAppId = steamAppId != 0
                ? steamAppId
                : current?.SteamAppId ?? 0;
            var workerStartedAtUtc =
                effectiveWorkerProcessId <= 0
                    ? null
                    : current?.WorkerProcessId == effectiveWorkerProcessId &&
                      current.WorkerStartedAtUtc.HasValue
                        ? current.WorkerStartedAtUtc
                        : TryGetProcessStartedAtUtc(effectiveWorkerProcessId);
            var now = DateTimeOffset.UtcNow;
            var metadataChanged =
                !string.Equals(
                    current?.GameTitle ?? string.Empty,
                    effectiveGameTitle,
                    StringComparison.Ordinal) ||
                (current?.SteamAppId ?? 0) != effectiveSteamAppId ||
                current?.WorkerProcessId != effectiveWorkerProcessId ||
                current?.WorkerStartedAtUtc != workerStartedAtUtc;

            // Download helpers can emit several progress lines per frame on a
            // fast connection. The UI polls at two seconds, so persisting every
            // line only creates cross-process mutex and disk pressure. Terminal
            // transitions, worker handoffs and metadata changes remain immediate.
            if (!metadataChanged &&
                current is not null &&
                normalizedStatus == "downloading" &&
                current.Status == normalizedStatus &&
                now - current.UpdatedAtUtc < ProgressPersistenceInterval)
            {
                return;
            }

            entries[key] = new UnifySteamDownloadStatus(
                normalizedStatus,
                Math.Clamp(progressPercent, 0, 100),
                detailText.Trim(),
                now,
                effectiveWorkerProcessId,
                Math.Max(0, downloadedBytes),
                Math.Max(0, totalBytes),
                Math.Max(0, downloadBytesPerSecond),
                Math.Max(0, decompressedBytesPerSecond),
                Math.Max(0, diskWriteBytesPerSecond),
                Math.Max(0, diskReadBytesPerSecond),
                Math.Max(1, attempt),
                effectiveGameTitle,
                effectiveSteamAppId,
                workerStartedAtUtc);
            WriteAllUnlocked(entries);
        });
    }

    public static void Clear(string storeId, string gameId)
    {
        WithLock(() =>
        {
            var entries = ReadAllUnlocked();
            if (entries.Remove(BuildKey(storeId, gameId)))
            {
                WriteAllUnlocked(entries);
            }
        });
    }

    private static Dictionary<string, UnifySteamDownloadStatus> ReadAllUnlocked()
    {
        if (TryReadAllUnlocked(FilePath, out var entries))
        {
            return entries;
        }

        return TryReadAllUnlocked(BackupFilePath, out entries)
            ? entries
            : new Dictionary<string, UnifySteamDownloadStatus>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryReadAllUnlocked(
        string path,
        out Dictionary<string, UnifySteamDownloadStatus> entries)
    {
        entries = new Dictionary<string, UnifySteamDownloadStatus>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json) || json.IndexOf('\0') >= 0)
            {
                return false;
            }

            var deserialized =
                JsonSerializer.Deserialize<Dictionary<string, UnifySteamDownloadStatus>>(
                    json,
                    JsonOptions);
            if (deserialized is null)
            {
                return false;
            }

            entries = new Dictionary<string, UnifySteamDownloadStatus>(
                deserialized,
                StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAllUnlocked(Dictionary<string, UnifySteamDownloadStatus> entries)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"omnilibrary-downloads-{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, JsonOptions));
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath) && TryReadAllUnlocked(FilePath, out _))
            {
                try
                {
                    File.Replace(
                        temporaryPath,
                        FilePath,
                        BackupFilePath,
                        ignoreMetadataErrors: true);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
                catch (IOException)
                {
                }
            }

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
                throw new TimeoutException("The OmniLibrary download status file is busy.");
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

    private static string BuildKey(string storeId, string gameId)
    {
        return $"{storeId.Trim().ToLowerInvariant()}:{gameId.Trim()}";
    }

    public static bool TryParseKey(
        string? key,
        out string storeId,
        out string gameId)
    {
        storeId = string.Empty;
        gameId = string.Empty;
        var normalizedKey = key ?? string.Empty;
        var separator = normalizedKey.IndexOf(':');
        if (separator <= 0 || separator >= normalizedKey.Length - 1)
        {
            return false;
        }

        storeId = normalizedKey[..separator].Trim().ToLowerInvariant();
        gameId = normalizedKey[(separator + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(storeId) &&
               !string.IsNullOrWhiteSpace(gameId);
    }

    private static bool IsProcessRunning(
        int processId,
        DateTimeOffset statusUpdatedAtUtc,
        DateTimeOffset? expectedStartedAtUtc = null)
    {
        try
        {
            // PIDs can be reused after Windows restarts. A status written
            // before the current boot can never belong to a currently running
            // process, even if Windows has already assigned the same PID.
            var bootedAtUtc = DateTimeOffset.UtcNow -
                              TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (statusUpdatedAtUtc < bootedAtUtc - TimeSpan.FromMinutes(1))
            {
                return false;
            }

            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            if (!expectedStartedAtUtc.HasValue)
            {
                return true;
            }

            var actualStartedAtUtc =
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return Math.Abs(
                       (actualStartedAtUtc - expectedStartedAtUtc.Value).TotalSeconds) <
                   2;
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? TryGetProcessStartedAtUtc(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited
                ? null
                : new DateTimeOffset(
                    process.StartTime.ToUniversalTime(),
                    TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }
}
