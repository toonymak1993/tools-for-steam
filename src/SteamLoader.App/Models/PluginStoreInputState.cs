namespace SteamLoader.App.Models;

public sealed record PluginStoreInputState(
    long Nonce,
    string Action,
    string Source);

public sealed record PluginStoreInputBatch(
    long LatestNonce,
    IReadOnlyList<PluginStoreInputState> Inputs);
