using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal static class GogOperationPhases
{
    public const string Preparing = "preparing";
    public const string Downloading = "downloading";
    public const string FilesVerified = "files-verified";
    public const string WindowsSetup = "windows-setup";
    public const string WaitingForGalaxy = "waiting-for-galaxy";
    public const string Ready = "ready";
    public const string Uninstalling = "uninstalling";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

internal sealed record GogOperationTransaction(
    string GameId,
    string Operation,
    string Phase,
    string ResumePhase,
    string InstallRoot,
    bool ManagedByOmniLibrary,
    bool IncludeDlc,
    string BuildId,
    long DownloadedBytes,
    long TotalBytes,
    int Attempt,
    string DetailText,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool IsInstall =>
        Operation.Equals("install", StringComparison.OrdinalIgnoreCase) ||
        Operation.Equals("update", StringComparison.OrdinalIgnoreCase) ||
        Operation.Equals("repair", StringComparison.OrdinalIgnoreCase);

    public bool IsUpdate =>
        Operation.Equals("update", StringComparison.OrdinalIgnoreCase);

    public bool IsRepair =>
        Operation.Equals("repair", StringComparison.OrdinalIgnoreCase);

    public bool IsUninstall =>
        Operation.Equals("uninstall", StringComparison.OrdinalIgnoreCase);

    public bool IsRecoverableInstall =>
        IsInstall &&
        Phase is not GogOperationPhases.Ready and
            not GogOperationPhases.Canceled &&
        (
            ManagedByOmniLibrary ||
            Phase == GogOperationPhases.WaitingForGalaxy
        );
}

/// <summary>
/// Durable, per-game GOG operation state. Download status is deliberately a
/// separate UI concern: this journal answers which installation phase TFS owns
/// after a process, Steam, or Windows restart.
/// </summary>
internal static class GogOperationJournal
{
    private const string MutexName = @"Local\ToolsForSteamOmniLibraryGogTransactions";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private static string FilePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "omnilibrary",
            "gog",
            "transactions.json");

    private static string BackupFilePath => $"{FilePath}.bak";

    public static GogOperationTransaction? Get(string gameId)
    {
        if (!IsSafeGameId(gameId))
        {
            return null;
        }

        GogOperationTransaction? result = null;
        WithLock(() =>
        {
            ReadAllUnlocked().TryGetValue(gameId.Trim(), out result);
        });
        return result;
    }

    public static IReadOnlyList<GogOperationTransaction> GetAll()
    {
        var result = Array.Empty<GogOperationTransaction>();
        WithLock(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var transactions = ReadAllUnlocked();
            var changed = false;
            foreach (var key in transactions.Keys.ToArray())
            {
                var transaction = transactions[key];
                var age = now - transaction.UpdatedAtUtc;
                if ((transaction.Phase is GogOperationPhases.Ready or GogOperationPhases.Canceled &&
                     age > TimeSpan.FromDays(1)) ||
                    (transaction.Phase == GogOperationPhases.Failed &&
                     age > TimeSpan.FromDays(30)))
                {
                    transactions.Remove(key);
                    changed = true;
                }
            }

            if (changed)
            {
                WriteAllUnlocked(transactions);
            }

            result = transactions.Values
                .OrderBy(transaction => transaction.CreatedAtUtc)
                .ToArray();
        });
        return result;
    }

    public static GogOperationTransaction BeginInstall(
        string gameId,
        string installRoot,
        bool includeDlc,
        bool managedByOmniLibrary = true,
        string operation = "install")
    {
        EnsureSafeGameId(gameId);
        var normalizedRoot = NormalizeRoot(installRoot);
        var normalizedOperation = NormalizeInstallOperation(operation);
        var now = DateTimeOffset.UtcNow;
        var transaction = new GogOperationTransaction(
            gameId.Trim(),
            normalizedOperation,
            managedByOmniLibrary
                ? GogOperationPhases.Preparing
                : GogOperationPhases.WaitingForGalaxy,
            managedByOmniLibrary
                ? GogOperationPhases.Preparing
                : GogOperationPhases.WaitingForGalaxy,
            normalizedRoot,
            managedByOmniLibrary,
            includeDlc,
            string.Empty,
            0,
            0,
            1,
            managedByOmniLibrary
                ? normalizedOperation switch
                {
                    "repair" => "Preparing the managed GOG repair.",
                    "update" => "Preparing the managed GOG update.",
                    _ => "Preparing the managed GOG installation.",
                }
                : "Waiting for GOG Galaxy to finish the installation.",
            now,
            now);
        Upsert(transaction);
        return transaction;
    }

    public static GogOperationTransaction BeginUninstall(
        string gameId,
        string installRoot,
        bool managedByOmniLibrary)
    {
        EnsureSafeGameId(gameId);
        var now = DateTimeOffset.UtcNow;
        var transaction = new GogOperationTransaction(
            gameId.Trim(),
            "uninstall",
            managedByOmniLibrary
                ? GogOperationPhases.Uninstalling
                : GogOperationPhases.WaitingForGalaxy,
            managedByOmniLibrary
                ? GogOperationPhases.Uninstalling
                : GogOperationPhases.WaitingForGalaxy,
            NormalizeRoot(installRoot),
            managedByOmniLibrary,
            false,
            string.Empty,
            0,
            0,
            1,
            managedByOmniLibrary
                ? "Removing the OmniLibrary-managed GOG installation."
                : "Waiting for GOG Galaxy to finish uninstalling the game.",
            now,
            now);
        Upsert(transaction);
        return transaction;
    }

    public static GogOperationTransaction Advance(
        string gameId,
        string phase,
        string? installRoot = null,
        string? buildId = null,
        long? downloadedBytes = null,
        long? totalBytes = null,
        int? attempt = null,
        string? detailText = null)
    {
        EnsureSafeGameId(gameId);
        GogOperationTransaction result = null!;
        WithLock(() =>
        {
            var transactions = ReadAllUnlocked();
            var now = DateTimeOffset.UtcNow;
            transactions.TryGetValue(gameId.Trim(), out var current);
            current ??= new GogOperationTransaction(
                gameId.Trim(),
                "install",
                GogOperationPhases.Preparing,
                GogOperationPhases.Preparing,
                NormalizeRoot(installRoot),
                true,
                false,
                string.Empty,
                0,
                0,
                1,
                string.Empty,
                now,
                now);

            var normalizedPhase = NormalizePhase(phase);
            var resumePhase = normalizedPhase == GogOperationPhases.Failed
                ? current.Phase == GogOperationPhases.Failed
                    ? current.ResumePhase
                    : current.Phase
                : normalizedPhase;
            result = current with
            {
                Phase = normalizedPhase,
                ResumePhase = resumePhase,
                InstallRoot = installRoot is null
                    ? current.InstallRoot
                    : NormalizeRoot(installRoot),
                BuildId = buildId is null ? current.BuildId : buildId.Trim(),
                DownloadedBytes = Math.Max(
                    current.DownloadedBytes,
                    Math.Max(0, downloadedBytes ?? current.DownloadedBytes)),
                TotalBytes = Math.Max(0, totalBytes ?? current.TotalBytes),
                Attempt = Math.Max(1, attempt ?? current.Attempt),
                DetailText = detailText is null
                    ? current.DetailText
                    : detailText.Trim(),
                UpdatedAtUtc = now,
            };
            transactions[gameId.Trim()] = result;
            WriteAllUnlocked(transactions);
        });
        return result;
    }

    public static void Fail(string gameId, string detailText)
    {
        if (Get(gameId) is not null)
        {
            Advance(gameId, GogOperationPhases.Failed, detailText: detailText);
        }
    }

    public static void Clear(string gameId)
    {
        if (!IsSafeGameId(gameId))
        {
            return;
        }

        WithLock(() =>
        {
            var transactions = ReadAllUnlocked();
            if (transactions.Remove(gameId.Trim()))
            {
                WriteAllUnlocked(transactions);
            }
        });
    }

    private static void Upsert(GogOperationTransaction transaction)
    {
        WithLock(() =>
        {
            var transactions = ReadAllUnlocked();
            transactions[transaction.GameId] = transaction;
            WriteAllUnlocked(transactions);
        });
    }

    private static Dictionary<string, GogOperationTransaction> ReadAllUnlocked()
    {
        if (TryRead(FilePath, out var transactions))
        {
            return transactions;
        }

        return TryRead(BackupFilePath, out transactions)
            ? transactions
            : new Dictionary<string, GogOperationTransaction>(
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryRead(
        string path,
        out Dictionary<string, GogOperationTransaction> transactions)
    {
        transactions = new Dictionary<string, GogOperationTransaction>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var payload = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(payload) || payload.IndexOf('\0') >= 0)
            {
                return false;
            }

            var parsed =
                JsonSerializer.Deserialize<Dictionary<string, GogOperationTransaction>>(
                    payload,
                    JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            transactions = parsed
                .Where(pair => IsSafeGameId(pair.Key) && pair.Value is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAllUnlocked(
        Dictionary<string, GogOperationTransaction> transactions)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"transactions-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(transactions, JsonOptions));
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

            if (File.Exists(FilePath))
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
                throw new TimeoutException(
                    "The GOG operation journal is busy.");
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

    private static string NormalizePhase(string phase)
    {
        var normalized = (phase ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            GogOperationPhases.Preparing or
            GogOperationPhases.Downloading or
            GogOperationPhases.FilesVerified or
            GogOperationPhases.WindowsSetup or
            GogOperationPhases.WaitingForGalaxy or
            GogOperationPhases.Ready or
            GogOperationPhases.Uninstalling or
            GogOperationPhases.Failed or
            GogOperationPhases.Canceled => normalized,
            _ => throw new InvalidOperationException(
                "The GOG operation phase is invalid."),
        };
    }

    private static string NormalizeInstallOperation(string? operation)
    {
        return operation?.Trim().ToLowerInvariant() switch
        {
            "repair" => "repair",
            "update" => "update",
            _ => "install",
        };
    }

    private static string NormalizeRoot(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path.Trim());
    }

    private static bool IsSafeGameId(string? gameId)
    {
        return !string.IsNullOrWhiteSpace(gameId) &&
               gameId.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    private static void EnsureSafeGameId(string gameId)
    {
        if (!IsSafeGameId(gameId))
        {
            throw new InvalidOperationException(
                "The GOG game identifier is invalid.");
        }
    }
}
