using System.Globalization;

namespace SteamLoader.App.Services;

internal sealed class SteamAutomaticRecoveryGuard
{
    internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(10);

    private const string RecoveryMutexName = @"Local\ToolsForSteam.SteamAutomaticRecovery";
    private readonly string _statePath;
    private readonly TimeSpan _cooldown;

    public SteamAutomaticRecoveryGuard(
        string? statePath = null,
        TimeSpan? cooldown = null)
    {
        _statePath = statePath ?? Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "steam-automatic-recovery.state");
        _cooldown = cooldown ?? DefaultCooldown;
    }

    public bool TryBegin(DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        using var recoveryMutex = new Mutex(false, RecoveryMutexName);
        var ownsMutex = false;

        try
        {
            try
            {
                ownsMutex = recoveryMutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                retryAfter = TimeSpan.FromSeconds(2);
                return false;
            }

            var previousAttempt = ReadPreviousAttempt();
            if (previousAttempt.HasValue)
            {
                var elapsed = now - previousAttempt.Value;
                if (elapsed < TimeSpan.Zero)
                {
                    // A clock correction must not reset the recovery budget or
                    // leave a future-dated marker blocking recovery forever.
                    WriteAttempt(now);
                    retryAfter = _cooldown;
                    return false;
                }

                if (elapsed < _cooldown)
                {
                    retryAfter = _cooldown - elapsed;
                    return false;
                }
            }

            WriteAttempt(now);
            return true;
        }
        catch (Exception exception)
        {
            // Failing closed is important here: an unwritable guard must not turn
            // into an unlimited kill/relaunch loop.
            SteamStartupDiagnostics.Write($"automatic Steam recovery guard failed closed: {exception.Message}");
            retryAfter = _cooldown;
            return false;
        }
        finally
        {
            if (ownsMutex)
            {
                recoveryMutex.ReleaseMutex();
            }
        }
    }

    public void MarkHealthy()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (Exception exception)
        {
            SteamStartupDiagnostics.Write($"automatic Steam recovery guard reset failed: {exception.Message}");
        }
    }

    private DateTimeOffset? ReadPreviousAttempt()
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        var value = File.ReadAllText(_statePath).Trim();
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private void WriteAttempt(DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _statePath,
            now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
