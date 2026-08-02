using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record OmniLibraryAchievementSourceContext(
    UnifySteamGameDetailSnapshot GameDetail,
    UnifySteamStoreConfiguration Store,
    OmniLibraryGameDataProviderConfiguration Provider,
    OmniLibraryAchievementMetadata Previous,
    string PreviousProviderState,
    bool RefreshDefinitions,
    bool RefreshProgress);

internal interface IOmniLibraryAchievementSource
{
    string ProviderId { get; }

    Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken);
}

internal sealed class DelegatingOmniLibraryAchievementSource(
    string providerId,
    Func<OmniLibraryAchievementSourceContext, CancellationToken,
        Task<OmniLibraryAchievementRefreshResult>> refresh)
    : IOmniLibraryAchievementSource
{
    public string ProviderId { get; } = providerId;

    public Task<OmniLibraryAchievementRefreshResult> RefreshAsync(
        OmniLibraryAchievementSourceContext context,
        CancellationToken cancellationToken) =>
        refresh(context, cancellationToken);
}
