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
            if (!File.Exists(_path) && !File.Exists($"{_path}.bak"))
            {
                return new StoreConfiguration();
            }

            if (TryReadConfiguration(_path, out var configuration))
            {
                return configuration;
            }

            var backupPath = $"{_path}.bak";
            if (TryReadConfiguration(backupPath, out configuration))
            {
                try
                {
                    File.Copy(backupPath, _path, overwrite: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                return configuration;
            }

            return new StoreConfiguration();
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
            if (File.Exists(_path)) File.Copy(_path, $"{_path}.bak", overwrite: true);
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

    private static string NormalizeAlertMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "discount" => "discount",
        "new-low" => "new-low",
        "release" => "release",
        _ => "price"
    };

    private static bool TryReadConfiguration(string path, out StoreConfiguration configuration)
    {
        configuration = new StoreConfiguration();
        if (!File.Exists(path)) return false;
        try
        {
            configuration = JsonSerializer.Deserialize<StoreConfiguration>(File.ReadAllText(path), JsonOptions)
                ?? new StoreConfiguration();
            configuration.Alerts ??= [];
            foreach (var alert in configuration.Alerts.Values)
            {
                alert.PriceHistory ??= [];
                alert.Mode = NormalizeAlertMode(alert.Mode);
                if (string.IsNullOrWhiteSpace(alert.GameId) && alert.SteamAppId > 0)
                    alert.GameId = $"steam:{alert.SteamAppId}";
            }
            configuration.SavedGames = new(
                configuration.SavedGames ?? new Dictionary<string, StoreGameState>(),
                StringComparer.OrdinalIgnoreCase);
            configuration.WishlistMetadata = new(
                configuration.WishlistMetadata ?? new Dictionary<string, StoreWishlistMetadataData>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var metadata in configuration.WishlistMetadata.Values)
            {
                metadata.Tags ??= [];
                metadata.Tags = metadata.Tags
                    .Select(tag => tag?.Trim() ?? string.Empty)
                    .Where(tag => tag.Length is > 0 and <= 32)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();
            }
            configuration.GameHistory = new(
                configuration.GameHistory ?? new Dictionary<string, StoreGameHistoryData>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var history in configuration.GameHistory.Values)
            {
                history.PriceHistory ??= [];
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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

    public Dictionary<string, StoreWishlistMetadataData> WishlistMetadata { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, StoreGameHistoryData> GameHistory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? LastSeenChangesUtc { get; set; }

    public bool IncludeKeyshops { get; set; } = true;

    public int ArtworkCacheMaximumMegabytes { get; set; } = 256;

    public int ArtworkCacheRetentionDays { get; set; } = 45;
}

public sealed class StoreWishlistMetadataData
{
    public DateTimeOffset? AddedAtUtc { get; set; }

    public bool IsPinned { get; set; }

    public List<string> Tags { get; set; } = [];
}

public sealed class StoreGameHistoryData
{
    public string GameId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset? StartedAtUtc { get; set; }

    public decimal? OriginalPrice { get; set; }

    public decimal? OriginalPriceEur { get; set; }

    public decimal? LowestPrice { get; set; }

    public decimal? LowestPriceEur { get; set; }

    public decimal? LastPrice { get; set; }

    public decimal? LastPriceEur { get; set; }

    public int LastDiscountPercent { get; set; }

    public int LastOfferCount { get; set; }

    public string LastBestStoreName { get; set; } = string.Empty;

    public bool WasOnSale { get; set; }

    public bool WasUnreleased { get; set; }

    public string ChangeKind { get; set; } = string.Empty;

    public DateTimeOffset? ChangedAtUtc { get; set; }

    public DateTimeOffset? LastCheckedAtUtc { get; set; }

    public List<StorePriceHistoryData> PriceHistory { get; set; } = [];
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

    public string Mode { get; set; } = "price";

    public int TargetDiscountPercent { get; set; }

    public DateTimeOffset? SnoozedUntilUtc { get; set; }
}

public sealed class StorePriceHistoryData
{
    public DateTimeOffset RecordedAtUtc { get; set; }

    public decimal? Price { get; set; }

    public decimal? PriceEur { get; set; }
}
