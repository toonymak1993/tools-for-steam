using System.Collections.Concurrent;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Serializes work for one OmniLibrary provider without making an unrelated
/// provider wait. Multi-store operations acquire gates in registry order so
/// they cannot deadlock with another broad refresh.
/// </summary>
internal sealed class OmniLibraryStoreGateSet
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Enter(string storeId, CancellationToken cancellationToken = default) =>
        EnterMany([storeId], cancellationToken);

    public IDisposable EnterMany(
        IEnumerable<string> storeIds,
        CancellationToken cancellationToken = default)
    {
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var storeId in NormalizeStoreIds(storeIds))
            {
                var gate = _gates.GetOrAdd(storeId, static _ => new SemaphoreSlim(1, 1));
                gate.Wait(cancellationToken);
                acquired.Add(gate);
            }

            return new Releaser(acquired);
        }
        catch
        {
            ReleaseReverse(acquired);
            throw;
        }
    }

    internal static IReadOnlyList<string> NormalizeStoreIds(IEnumerable<string> storeIds)
    {
        var order = OmniLibraryStoreRegistry.All
            .Select((store, index) => (store.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return storeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => order.TryGetValue(id, out var index) ? index : int.MaxValue)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReleaseReverse(IReadOnlyList<SemaphoreSlim> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--)
        {
            gates[index].Release();
        }
    }

    private sealed class Releaser(IReadOnlyList<SemaphoreSlim> gates) : IDisposable
    {
        private IReadOnlyList<SemaphoreSlim>? _gates = gates;

        public void Dispose()
        {
            var gatesToRelease = Interlocked.Exchange(ref _gates, null);
            if (gatesToRelease is not null)
            {
                ReleaseReverse(gatesToRelease);
            }
        }
    }
}
