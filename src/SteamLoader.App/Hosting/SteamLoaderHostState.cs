using SteamLoader.App.Services;

namespace SteamLoader.App.Hosting;

public sealed class SteamLoaderHostState
{
    private readonly object _gate = new();

    private bool _sharedContextAttached;
    private bool _quickAccessAttached;
    private string _serviceMessage = "Starting background host...";
    private string? _lastError;
    private SteamClientStartupStage _steamStartupStage = SteamClientStartupStage.Starting;
    private SteamUiState _steamUiState = SteamUiState.Starting;

    public SteamLoaderHostState()
    {
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset StartedAtUtc { get; }

    public void UpdateSharedContext(bool attached, string message)
    {
        lock (_gate)
        {
            _sharedContextAttached = attached;
            _serviceMessage = message;

            if (attached)
            {
                _lastError = null;
            }
        }
    }

    public void UpdateQuickAccess(bool attached, string message)
    {
        lock (_gate)
        {
            _quickAccessAttached = attached;
            _serviceMessage = message;

            if (attached)
            {
                _lastError = null;
            }
        }
    }

    public void UpdateMessage(string message)
    {
        lock (_gate)
        {
            _serviceMessage = message;
        }
    }

    public void UpdateSteamStartup(SteamClientStartupStage stage, string message)
    {
        lock (_gate)
        {
            _steamStartupStage = stage;
            _serviceMessage = message;

            if (stage == SteamClientStartupStage.Ready)
            {
                _lastError = null;
            }
        }
    }

    public void UpdateSteamUiState(SteamUiState state)
    {
        lock (_gate)
        {
            _steamUiState = state;
        }
    }

    public void UpdateError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
            _serviceMessage = message;
        }
    }

    public SteamLoaderHostStatus Snapshot()
    {
        lock (_gate)
        {
            return new SteamLoaderHostStatus(
                StartedAtUtc,
                _sharedContextAttached,
                _quickAccessAttached,
                _serviceMessage,
                _lastError,
                _steamStartupStage,
                _steamUiState);
        }
    }
}

public sealed record SteamLoaderHostStatus(
    DateTimeOffset StartedAtUtc,
    bool SharedContextAttached,
    bool QuickAccessAttached,
    string ServiceMessage,
    string? LastError,
    SteamClientStartupStage SteamStartupStage = SteamClientStartupStage.Starting,
    SteamUiState SteamUiState = SteamUiState.Starting);
