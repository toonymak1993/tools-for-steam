using System.Text.Json;
using SteamLoader.App.Infrastructure.Audio;
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

    private readonly QuickAccessLiveUpdateHub _liveUpdateHub;
    private readonly IAudioOutputDeviceService _audioOutputDeviceService;
    private readonly ProcessWindowService _processWindowService;
    private readonly StoreSyncService _storeSyncService;
    private readonly SmartHomeService _smartHomeService;
    private readonly Func<bool> _isSmartHomeEnabled;

    private string _lastProcessesFingerprint = string.Empty;
    private string _lastAudioDashboardFingerprint = string.Empty;
    private string _lastStoreSyncFingerprint = string.Empty;
    private string _lastSmartHomeFingerprint = string.Empty;

    public QuickAccessLiveStatePublisher(
        QuickAccessLiveUpdateHub liveUpdateHub,
        IAudioOutputDeviceService audioOutputDeviceService,
        ProcessWindowService processWindowService,
        StoreSyncService storeSyncService,
        SmartHomeService smartHomeService,
        Func<bool> isSmartHomeEnabled)
    {
        _liveUpdateHub = liveUpdateHub;
        _audioOutputDeviceService = audioOutputDeviceService;
        _processWindowService = processWindowService;
        _storeSyncService = storeSyncService;
        _smartHomeService = smartHomeService;
        _isSmartHomeEnabled = isSmartHomeEnabled;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextProcessesAtUtc = DateTimeOffset.UtcNow;
        var nextAudioAtUtc = DateTimeOffset.UtcNow;
        var nextStoreSyncAtUtc = DateTimeOffset.UtcNow;
        var nextSmartHomeAtUtc = DateTimeOffset.UtcNow;

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

            await Task.Delay(LoopInterval, cancellationToken);
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
