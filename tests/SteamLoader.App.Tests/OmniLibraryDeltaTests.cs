using SteamLoader.App.Infrastructure.StoreSync;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class OmniLibraryDeltaTests
{
    [Fact]
    public void ComputeCatalogDelta_ReturnsOnlyAddedAndRemovedGameIds()
    {
        var delta = StoreSyncService.ComputeOmniLibraryCatalogDelta(
            ["unchanged", "removed", "CASE-ID"],
            ["unchanged", "added", "case-id"]);

        Assert.Equal(["added"], delta.AddedGameIds);
        Assert.Equal(["removed"], delta.RemovedGameIds);
    }

    [Fact]
    public void ComputeCatalogDelta_InitialConnectionAddsEntireCatalog()
    {
        var delta = StoreSyncService.ComputeOmniLibraryCatalogDelta(
            [],
            ["game-b", "game-a"]);

        Assert.Equal(["game-a", "game-b"], delta.AddedGameIds);
        Assert.Empty(delta.RemovedGameIds);
    }

    [Fact]
    public void ComputeChangedGameIds_ReturnsOnlyExistingGamesWhoseMetadataChanged()
    {
        var before = CreateStore(
            new UnifySteamGameCacheEntry { Id = "same", Title = "Same", Version = "1" },
            new UnifySteamGameCacheEntry { Id = "changed", Title = "Old", Version = "1" },
            new UnifySteamGameCacheEntry { Id = "removed", Title = "Removed" });
        var after = CreateStore(
            new UnifySteamGameCacheEntry { Id = "same", Title = "Same", Version = "1" },
            new UnifySteamGameCacheEntry { Id = "changed", Title = "New", Version = "2" },
            new UnifySteamGameCacheEntry { Id = "added", Title = "Added" });

        var changed = StoreSyncService.ComputeOmniLibraryChangedGameIds(before, after);

        Assert.Equal(["changed"], changed);
    }

    private static UnifySteamStoreConfiguration CreateStore(params UnifySteamGameCacheEntry[] games)
    {
        return new UnifySteamStoreConfiguration
        {
            Cache = new UnifySteamLibraryCache { Games = games.ToList() },
        };
    }
}
