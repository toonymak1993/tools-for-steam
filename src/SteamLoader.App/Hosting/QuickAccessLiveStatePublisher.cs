using System.Text.Json;
using SteamLoader.App.Infrastructure.Audio;
using SteamLoader.App.Infrastructure.Discord;
using SteamLoader.App.Infrastructure.Handheld;
using SteamLoader.App.Infrastructure.Processes;
using SteamLoader.App.Infrastructure.SmartHome;
using SteamLoader.App.Infrastructure.StoreSync;
using SteamLoader.App.Models;

namespace SteamLoader.App.Hosting;

public sealed class QuickAccessLiveStatePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProcessesInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AudioInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StoreSyncInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SmartHomeInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandheldPerformanceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscordPresenceInterval = TimeSpan.FromSeconds(15);

    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;
    private readonly IAudioOutputDeviceService _audioOutputDeviceService;
    private readonly ProcessWindowService _processWindowService;
    private readonly StoreSyncService _storeSyncService;
    private readonly SmartHomeService _smartHomeService;
    private readonly Func<bool> _isSmartHomeEnabled;
    private readonly HandheldPerformanceService _handheldPerformanceService;
    private readonly DiscordService _discordService;
    private readonly Func<bool> _isDiscordEnabled;
    private readonly DiscordFriendPresenceTracker _discordPresenceTracker = new();

    private string _lastProcessesFingerprint = string.Empty;
    private string _lastAudioDashboardFingerprint = string.Empty;
    private string _lastStoreSyncFingerprint = string.Empty;
    private string _lastSmartHomeFingerprint = string.Empty;
    private string _lastHandheldPerformanceFingerprint = string.Empty;
    private string _lastDiscordFingerprint = string.Empty;

    public QuickAccessLiveStatePublisher(
        QuickAccessLiveUpdateHub liveUpdateHub,
        IAudioOutputDeviceService audioOutputDeviceService,
        ProcessWindowService processWindowService,
        StoreSyncService storeSyncService,
        SmartHomeService smartHomeService,
        HandheldPerformanceService handheldPerformanceService,
        Func<bool> isSmartHomeEnabled,
        DiscordService discordService,
        Func<bool> isDiscordEnabled)
    {
        _liveUpdateHub = liveUpdateHub;
        _audioOutputDeviceService = audioOutputDeviceService;
        _processWindowService = processWindowService;
        _storeSyncService = storeSyncService;
        _smartHomeService = smartHomeService;
        _handheldPerformanceService = handheldPerformanceService;
        _isSmartHomeEnabled = isSmartHomeEnabled;
        _discordService = discordService;
        _isDiscordEnabled = isDiscordEnabled;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var discordPresenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var discordPresenceTask = RunDiscordPresenceMonitorAsync(discordPresenceCts.Token);
        var nextProcessesAtUtc = DateTimeOffset.UtcNow;
        var nextAudioAtUtc = DateTimeOffset.UtcNow;
        var nextStoreSyncAtUtc = DateTimeOffset.UtcNow;
        var nextSmartHomeAtUtc = DateTimeOffset.UtcNow;
        var nextHandheldPerformanceAtUtc = DateTimeOffset.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;

                if (now >= nextProcessesAtUtc)
                {
                    await PublishProcessesStateIfChangedAsync(cancellationToken);
                    nextProcessesAtUtc = now.Add(ProcessesInterval);
                }

                if (now >= nextAudioAtUtc)
                {
                    await PublishAudioStateIfChangedAsync(cancellationToken);
                    nextAudioAtUtc = now.Add(AudioInterval);
                }

                if (now >= nextStoreSyncAtUtc)
                {
                    PublishStoreSyncStateIfChanged();
                    nextStoreSyncAtUtc = now.Add(StoreSyncInterval);
                }

                if (now >= nextSmartHomeAtUtc)
                {
                    await PublishSmartHomeStateIfChangedAsync(cancellationToken);
                    nextSmartHomeAtUtc = now.Add(SmartHomeInterval);
                }

                if (now >= nextHandheldPerformanceAtUtc)
                {
                    PublishHandheldPerformanceStateIfChanged();
                    nextHandheldPerformanceAtUtc = now.Add(HandheldPerformanceInterval);
                }

                await Task.Delay(LoopInterval, cancellationToken);
            }
        }
        finally
        {
            await discordPresenceCts.CancelAsync();
            try
            {
                await discordPresenceTask;
            }
            catch (OperationCanceledException) when (discordPresenceCts.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunDiscordPresenceMonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PublishDiscordPresenceAsync(cancellationToken);
            await Task.Delay(DiscordPresenceInterval, cancellationToken);
        }
    }

    private async Task PublishDiscordPresenceAsync(CancellationToken cancellationToken)
    {
        if (!_isDiscordEnabled() || !_discordService.AreFriendOnlineNotificationsEnabled())
        {
            _discordPresenceTracker.Reset();
            _lastDiscordFingerprint = string.Empty;
            return;
        }

        try
        {
            var snapshot = await _discordService.GetSnapshotAsync(forceRefresh: true, cancellationToken);
            var canObservePresence =
                snapshot.FriendOnlineNotificationsEnabled &&
                snapshot.Connected &&
                snapshot.Authorized &&
                snapshot.ConnectionMode.Equals("social-sdk", StringComparison.OrdinalIgnoreCase);
            var newlyOnline = _discordPresenceTracker.Observe(
                snapshot.Friends ?? [],
                canObservePresence);

            var normalizedSnapshot = snapshot with { RefreshedAtUtc = null };
            PublishIfChanged(
                "discord.state",
                normalizedSnapshot,
                snapshot,
                ref _lastDiscordFingerprint);

            if (newlyOnline.Count == 1)
            {
                var friend = newlyOnline[0];
                var displayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                    ? friend.Username
                    : friend.DisplayName;
                PublishDiscordNotification($"{displayName} is now online.");
            }
            else if (newlyOnline.Count > 1)
            {
                var names = newlyOnline
                    .Take(3)
                    .Select(friend => string.IsNullOrWhiteSpace(friend.DisplayName)
                        ? friend.Username
                        : friend.DisplayName)
                    .ToArray();
                var suffix = newlyOnline.Count > names.Length
                    ? $" and {newlyOnline.Count - names.Length} more"
                    : string.Empty;
                PublishDiscordNotification($"{string.Join(", ", names)}{suffix} are now online.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Presence monitoring must never interrupt other live Quick Access updates.
        }
    }

    private void PublishDiscordNotification(string message)
    {
        _liveUpdateHub.Publish(
            "notifications.show",
            new
            {
                title = "Discord",
                message,
                level = "info",
                durationMs = 5000
            });
    }

    private void PublishHandheldPerformanceStateIfChanged()
    {
        try
        {
            var snapshot = _handheldPerformanceService.GetSnapshot();
            if (!snapshot.Supported)
            {
                return;
            }

            PublishIfChanged(
                "handheld-performance.state",
                snapshot,
                snapshot,
                ref _lastHandheldPerformanceFingerprint);
        }
        catch
        {
        }
    }

    private Task PublishProcessesStateIfChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _processWindowService.GetSnapshot();
            PublishIfChanged(
                "processes.state",
                snapshot,
                snapshot,
                ref _lastProcessesFingerprint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    private async Task PublishAudioStateIfChangedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await StaThread.RunAsync(
                () => _audioOutputDeviceService.GetDashboardSnapshot(),
                cancellationToken);

            PublishIfChanged(
                "audio.dashboard",
                snapshot,
                snapshot,
                ref _lastAudioDashboardFingerprint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private void PublishStoreSyncStateIfChanged()
    {
        try
        {
            var snapshot = _storeSyncService.GetSnapshot();
            var normalizedSnapshot = snapshot with
            {
                Health = snapshot.Health with
                {
                    LastAutomaticCheckAtUtc = null,
                    LastAutomaticTriggerAtUtc = null
                }
            };

            PublishIfChanged(
                "store-sync.state",
                normalizedSnapshot,
                snapshot,
                ref _lastStoreSyncFingerprint);
        }
        catch
        {
        }
    }

    private async Task PublishSmartHomeStateIfChangedAsync(CancellationToken cancellationToken)
    {
        if (!_isSmartHomeEnabled())
        {
            return;
        }

        try
        {
            var snapshot = await _smartHomeService.GetSnapshotAsync(forceRefresh: false, cancellationToken);
            var normalizedSnapshot = snapshot with
            {
                RefreshedAtUtc = null
            };

            PublishIfChanged(
                "smart-home.state",
                normalizedSnapshot,
                snapshot,
                ref _lastSmartHomeFingerprint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private bool PublishIfChanged<TFingerprint, TPayload>(
        string topic,
        TFingerprint fingerprintSource,
        TPayload livePayload,
        ref string lastFingerprint)
    {
        var fingerprint = JsonSerializer.Serialize(fingerprintSource, JsonOptions);
        if (string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var shouldPublish = !string.IsNullOrEmpty(lastFingerprint);
        lastFingerprint = fingerprint;

        if (shouldPublish)
        {
            _liveUpdateHub.Publish(topic, livePayload);
        }

        return shouldPublish;
    }
}
