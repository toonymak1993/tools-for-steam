using System.Globalization;
using Microsoft.Win32;

namespace SteamLoader.App.Services;

/// <summary>
/// Small per-user state shared by the TFS processes (tray app, background host)
/// that decide whether the taskbar and desktop icons are hidden. It lives in
/// the registry so it survives a crash or restart of either process.
/// </summary>
internal sealed class ConsoleShellState
{
    private const string DefaultKeyPath = @"Software\GCM\SteamTools";
    private const string DesktopRequestedValueName = "ConsoleDesktopRequestedAtUtc";
    private const string ChromeHiddenValueName = "ShellChromeHiddenByTfs";
    private const string StartCoverOwnerValueName = "ConsoleStartCoverOwner";

    private readonly string _keyPath;

    public ConsoleShellState()
        : this(DefaultKeyPath)
    {
    }

    internal ConsoleShellState(string keyPath)
    {
        _keyPath = string.IsNullOrWhiteSpace(keyPath) ? DefaultKeyPath : keyPath;
    }

    /// <summary>
    /// The user asked for the Windows desktop. The guard keeps the taskbar
    /// visible until Big Picture is brought back to the foreground.
    /// </summary>
    public void RequestDesktop(DateTimeOffset requestedAt)
    {
        TryWrite(key => key.SetValue(
            DesktopRequestedValueName,
            requestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            RegistryValueKind.String));
    }

    public DateTimeOffset? GetDesktopRequest()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            var value = key?.GetValue(DesktopRequestedValueName) as string;
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestedAt)
                ? requestedAt
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void ClearDesktopRequest()
    {
        TryDelete(DesktopRequestedValueName);
    }

    /// <summary>
    /// Set while a TFS process keeps the taskbar hidden. Any TFS process that
    /// later finds it set, without a reason to hide, shows the taskbar again -
    /// even when the process that hid it crashed.
    /// </summary>
    public bool IsChromeMarkedHidden()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            return key?.GetValue(ChromeHiddenValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public void MarkChromeHidden(bool hidden)
    {
        if (hidden)
        {
            if (!IsChromeMarkedHidden())
            {
                TryWrite(key => key.SetValue(ChromeHiddenValueName, 1, RegistryValueKind.DWord));
            }

            return;
        }

        if (IsChromeMarkedHidden())
        {
            TryDelete(ChromeHiddenValueName);
        }
    }

    /// <summary>
    /// The console start splash owns the taskbar until its hand-off. The guard
    /// in the background host must not show the taskbar over the splash; if
    /// the owning process died, the marker no longer counts.
    /// </summary>
    public void SetStartCoverOwner(System.Diagnostics.Process owner)
    {
        var startTicks = TryGetStartTimeTicks(owner);
        TryWrite(key => key.SetValue(
            StartCoverOwnerValueName,
            string.Create(CultureInfo.InvariantCulture, $"{owner.Id}:{startTicks}"),
            RegistryValueKind.String));
    }

    public void ClearStartCoverOwner()
    {
        TryDelete(StartCoverOwnerValueName);
    }

    public bool IsStartCoverActive()
    {
        string? value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
            value = key?.GetValue(StartCoverOwnerValueName) as string;
        }
        catch
        {
            return false;
        }

        var parts = value?.Split(':');
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
        {
            return false;
        }

        try
        {
            // Process id plus start time: a reused id of another process (or of
            // a TFS process from an earlier sign-in) must not count as the owner.
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited && TryGetStartTimeTicks(process) == startTicks;
        }
        catch
        {
            return false;
        }
    }

    private static long TryGetStartTimeTicks(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private void TryWrite(Action<RegistryKey> write)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
            if (key is not null)
            {
                write(key);
            }
        }
        catch
        {
            // Best effort: losing this marker only delays a restore until the
            // next guard decision; it must never break the console start.
        }
    }

    private void TryDelete(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
