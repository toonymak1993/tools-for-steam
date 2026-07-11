using System.Windows.Forms;

namespace SteamLoader.App.Infrastructure.Handheld;

internal sealed class HandheldPowerStateReader
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private HandheldPowerState _cached = new();

    public HandheldPowerState Read(bool force = false)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && _cached.UpdatedAt != default && now - _cached.UpdatedAt < CacheDuration)
            {
                return _cached;
            }

            try
            {
                var status = SystemInformation.PowerStatus;
                var pluggedIn = status.PowerLineStatus != PowerLineStatus.Offline;
                var batteryPercent = status.BatteryLifePercent is >= 0 and <= 1
                    ? (int)Math.Round(status.BatteryLifePercent * 100)
                    : -1;
                var estimatedMinutes = status.BatteryLifeRemaining > 0
                    ? status.BatteryLifeRemaining / 60
                    : -1;
                _cached = new HandheldPowerState(
                    pluggedIn ? "ac" : "battery",
                    pluggedIn,
                    batteryPercent,
                    estimatedMinutes,
                    now);
            }
            catch
            {
                _cached = _cached with { UpdatedAt = now };
            }

            return _cached;
        }
    }
}
