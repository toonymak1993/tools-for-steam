using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed class XboxProductRelationState
{
    public Dictionary<string, XboxProductRelationEntry> Products { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class XboxProductRelationEntry
{
    public HashSet<string> PackageIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Persists the relation between the public Xbox catalog product and the
/// downloadable package selected by the Store. The two IDs are frequently
/// different for bundles and editions, but every other OmniLibrary component
/// must treat them as one game.
/// </summary>
internal static class XboxProductRelationStore
{
    private const string MutexName = @"Local\ToolsForSteamXboxProductRelations";
    private const int MaximumProducts = 4096;
    private const int MaximumPackagesPerProduct = 16;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(365);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object CacheGate = new();
    private static XboxProductRelationState? _cachedState;
    private static DateTimeOffset _cacheCheckedAtUtc = DateTimeOffset.MinValue;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "omnilibrary-xbox-product-relations.json");

    public static IReadOnlySet<string> GetRelatedProductIds(string catalogProductId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsSafeProductId(catalogProductId))
        {
            return result;
        }

        result.Add(catalogProductId);
        var state = ReadCached();
        if (state.Products.TryGetValue(catalogProductId, out var entry))
        {
            foreach (var packageId in entry.PackageIds.Where(IsSafeProductId))
            {
                result.Add(packageId);
            }
        }

        return result;
    }

    public static bool IsRelated(string catalogProductId, string packageProductId)
    {
        if (!IsSafeProductId(catalogProductId) ||
            !IsSafeProductId(packageProductId))
        {
            return false;
        }

        return GetRelatedProductIds(catalogProductId).Contains(packageProductId);
    }

    public static void Register(string catalogProductId, string packageProductId)
    {
        if (!IsSafeProductId(catalogProductId) ||
            !IsSafeProductId(packageProductId) ||
            string.Equals(
                catalogProductId,
                packageProductId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (GetRelatedProductIds(catalogProductId).Contains(packageProductId))
        {
            return;
        }

        WithLock(() =>
        {
            var state = ReadFileUnlocked();
            if (!state.Products.TryGetValue(catalogProductId, out var entry))
            {
                entry = new XboxProductRelationEntry();
                state.Products[catalogProductId] = entry;
            }

            entry.PackageIds = new HashSet<string>(
                entry.PackageIds.Where(IsSafeProductId),
                StringComparer.OrdinalIgnoreCase);
            if (!entry.PackageIds.Add(packageProductId))
            {
                UpdateCache(state);
                return;
            }

            if (entry.PackageIds.Count > MaximumPackagesPerProduct)
            {
                entry.PackageIds = entry.PackageIds
                    .OrderByDescending(id =>
                        string.Equals(id, packageProductId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumPackagesPerProduct)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Prune(state);
            WriteUnlocked(state);
        });
    }

    private static XboxProductRelationState ReadCached()
    {
        lock (CacheGate)
        {
            if (_cachedState is not null &&
                DateTimeOffset.UtcNow - _cacheCheckedAtUtc < CacheLifetime)
            {
                return _cachedState;
            }
        }

        var state = new XboxProductRelationState();
        WithLock(() => state = ReadFileUnlocked());
        UpdateCache(state);
        return state;
    }

    private static XboxProductRelationState ReadFileUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new XboxProductRelationState();
            }

            var state = JsonSerializer.Deserialize<XboxProductRelationState>(
                            File.ReadAllText(FilePath),
                            JsonOptions) ??
                        new XboxProductRelationState();
            state.Products = new Dictionary<string, XboxProductRelationEntry>(
                state.Products ?? [],
                StringComparer.OrdinalIgnoreCase);
            Prune(state);
            return state;
        }
        catch
        {
            return new XboxProductRelationState();
        }
    }

    private static void WriteUnlocked(XboxProductRelationState state)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"omnilibrary-xbox-product-relations-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
            UpdateCache(state);
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

    private static void UpdateCache(XboxProductRelationState state)
    {
        lock (CacheGate)
        {
            _cachedState = state;
            _cacheCheckedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static void Prune(XboxProductRelationState state)
    {
        var cutoff = DateTimeOffset.UtcNow - MaximumAge;
        foreach (var productId in state.Products.Keys.ToArray())
        {
            var entry = state.Products[productId];
            if (!IsSafeProductId(productId) ||
                entry is null ||
                entry.UpdatedAtUtc < cutoff)
            {
                state.Products.Remove(productId);
                continue;
            }

            entry.PackageIds = new HashSet<string>(
                (entry.PackageIds ?? [])
                .Where(IsSafeProductId)
                .Take(MaximumPackagesPerProduct),
                StringComparer.OrdinalIgnoreCase);
        }

        if (state.Products.Count <= MaximumProducts)
        {
            return;
        }

        state.Products = state.Products
            .OrderByDescending(pair => pair.Value.UpdatedAtUtc)
            .Take(MaximumProducts)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSafeProductId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 64 &&
               value.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
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

            if (lockTaken)
            {
                action();
            }
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
