using System.Text.Json;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Store;

public sealed class StoreSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    public StoreSettingsStore(string path)
    {
        _path = path;
    }

    public StoreConfiguration Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new StoreConfiguration();
            }

            try
            {
                var json = File.ReadAllText(_path);
                var configuration = JsonSerializer.Deserialize<StoreConfiguration>(json, JsonOptions)
                    ?? new StoreConfiguration();
                configuration.Alerts ??= [];
                foreach (var alert in configuration.Alerts.Values)
                {
                    alert.PriceHistory ??= [];
                    if (string.IsNullOrWhiteSpace(alert.GameId) && alert.SteamAppId > 0)
                        alert.GameId = $"steam:{alert.SteamAppId}";
                }
                configuration.SavedGames ??= new(StringComparer.OrdinalIgnoreCase);
                return configuration;
            }
            catch
            {
                return new StoreConfiguration();
            }
        }
    }

    public void Save(StoreConfiguration configuration)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{_path}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
    }

    public TResult Update<TResult>(Func<StoreConfiguration, TResult> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var configuration = Load();
            var result = update(configuration);
            Save(configuration);
            return result;
        }
    }
}

public sealed class StoreConfiguration
{
    public int RefreshIntervalMinutes { get; set; } = 30;

    public bool NotificationsEnabled { get; set; } = true;

    public string DisplayCurrencyCode { get; set; } = "USD";

    public string StoreRegionCode { get; set; } = "US";

    public DateTimeOffset? LastRefreshUtc { get; set; }

    public StoreSnapshot? CachedSnapshot { get; set; }

    public Dictionary<long, StorePriceAlertData> Alerts { get; set; } = [];

    public Dictionary<string, StoreGameState> SavedGames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StorePriceAlertData
{
    public long SteamAppId { get; set; }

    public string GameId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public decimal TargetPrice { get; set; }

    public string CurrencyCode { get; set; } = "EUR";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? CreatedAtUtc { get; set; }

    public decimal? OriginalPrice { get; set; }

    public decimal? OriginalPriceEur { get; set; }

    public List<StorePriceHistoryData> PriceHistory { get; set; } = [];

    public decimal? LastNotifiedPrice { get; set; }

    public bool WasReached { get; set; }
}

public sealed class StorePriceHistoryData
{
    public DateTimeOffset RecordedAtUtc { get; set; }

    public decimal? Price { get; set; }

    public decimal? PriceEur { get; set; }
}
