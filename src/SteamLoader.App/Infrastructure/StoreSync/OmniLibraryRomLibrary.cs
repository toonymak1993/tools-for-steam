namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Cheap local ROM catalog scanner. File size + last-write time is the delta
/// fingerprint; unchanged entries retain Steam IDs and downloaded metadata.
/// The scanner is always called from OmniLibrary's background refresh worker.
/// </summary>
internal static class OmniLibraryRomLibrary
{
    public static void Refresh(UnifySteamStoreConfiguration store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var root = OmniLibraryRomSystemRegistry.EnsureFolderStructure(
            store.InstallPath);
        store.InstallPath = root;

        var existingById = (store.Cache?.Games ?? [])
            .Where(game => game is not null && !string.IsNullOrWhiteSpace(game.Id))
            .GroupBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var games = new List<UnifySteamGameCacheEntry>();
        foreach (var system in OmniLibraryRomSystemRegistry.Supported)
        {
            var systemRoot = Path.Combine(root, system.FolderName);
            foreach (var path in EnumerateRomFiles(systemRoot, system.Extensions))
            {
                FileInfo file;
                try
                {
                    file = new FileInfo(path);
                    if (!IsReadyForImport(file))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(root, file.FullName);
                var id = OmniLibraryRomSystemRegistry.BuildStableGameId(
                    system,
                    relativePath);
                var fingerprint = OmniLibraryRomSystemRegistry.BuildFileFingerprint(file);
                existingById.TryGetValue(id, out var existing);
                var sidecarCover = OmniLibraryRomSystemRegistry.FindSidecarCover(file.FullName);
                var imageUrl = sidecarCover ?? (existing?.ImageUrl ?? string.Empty);
                if (sidecarCover is null &&
                    (string.IsNullOrWhiteSpace(imageUrl) ||
                     imageUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                    imageUrl.StartsWith(
                        "https://thumbnails.libretro.com/",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    imageUrl = OmniLibraryRomSystemRegistry.BuildLibretroBoxArtUrl(
                        system,
                        file.FullName);
                }
                var artworkFingerprint =
                    OmniLibraryRomSystemRegistry.BuildOptionalFileFingerprint(sidecarCover);

                games.Add(new UnifySteamGameCacheEntry
                {
                    Id = id,
                    Title = OmniLibraryRomSystemRegistry.BuildDisplayTitle(file.FullName),
                    Installed = true,
                    InstallPath = file.DirectoryName ?? systemRoot,
                    ExecutablePath = file.FullName,
                    ProviderGameId = relativePath,
                    HasInstallableAsset = false,
                    PreparationSignature = string.IsNullOrWhiteSpace(artworkFingerprint)
                        ? fingerprint
                        : $"{fingerprint}:{artworkFingerprint}",
                    ImageUrl = imageUrl,
                    SteamAppId = existing?.SteamAppId ?? 0,
                    PlatformId = system.Id,
                    PlatformTitle = system.Title,
                    RomPath = file.FullName,
                });
            }
        }

        games.Sort((left, right) =>
        {
            var systemComparison = string.Compare(
                left.PlatformTitle,
                right.PlatformTitle,
                StringComparison.OrdinalIgnoreCase);
            return systemComparison != 0
                ? systemComparison
                : string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        });

        var cache = store.Cache ??= new UnifySteamLibraryCache();
        cache.AccountName = "Local ROMs";
        cache.Games = games;
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        cache.DetailText = games.Count == 0
            ? $"Add games to the PSP, GameCube, Game Boy Advance, or Nintendo 64 folders in {root}."
            : $"{games.Count} ROM{(games.Count == 1 ? string.Empty : "s")} found across supported systems. Only file deltas are processed.";
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        store.RemoteCatalogItemIds = games.Select(game => game.Id).ToList();
        store.RemoteCatalogSignature = ComputeCatalogSignature(games);
    }

    private static bool IsReadyForImport(FileInfo file)
    {
        try
        {
            file.Refresh();
            if (!file.Exists ||
                file.Length <= 0 ||
                DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMilliseconds(750))
            {
                return false;
            }

            // Explorer and download tools normally keep the destination open
            // while copying. Avoid importing a partially written ISO; the final
            // file-system event (or the periodic delta scan) will retry it.
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return stream.Length == file.Length;
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

    private static IEnumerable<string> EnumerateRomFiles(
        string root,
        IReadOnlySet<string> extensions)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(
                    root,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        ReturnSpecialDirectories = false,
                    })
                .GetEnumerator();
            while (enumerator.MoveNext())
            {
                var path = enumerator.Current;
                if (extensions.Contains(Path.GetExtension(path)))
                {
                    yield return path;
                }
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    private static string ComputeCatalogSignature(
        IEnumerable<UnifySteamGameCacheEntry> games)
    {
        var material = string.Join(
            '\n',
            games.OrderBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
                .Select(game =>
                    $"{game.Id}\t{game.PreparationSignature}\t{game.Title}\t{game.ImageUrl}"));
        return string.IsNullOrWhiteSpace(material)
            ? string.Empty
            : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(material)));
    }
}
