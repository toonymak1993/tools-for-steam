namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Cheap local ROM catalog scanner. File size + last-write time is the delta
/// fingerprint; unchanged entries retain Steam IDs and downloaded metadata.
/// The scanner is always called from OmniLibrary's background refresh worker.
/// Sources are TFS' own ROM root (one folder per system) and, when enabled,
/// ORBIT's managed ROM folders, which are imported in place.
/// </summary>
internal static class OmniLibraryRomLibrary
{
    private const long MaximumPlaylistBytes = 64 * 1024;

    private sealed record RomSource(
        string Root,
        string IdPrefix,
        IReadOnlyList<(OmniLibraryRomSystemDescriptor System, string Folder)> Folders);

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
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orbitGames = 0;
        var emulatorListGames = 0;

        bool AddGame(OmniLibraryRomSystemDescriptor system, string path, string identityPath)
        {
            FileInfo file;
            try
            {
                file = new FileInfo(path);
                if (!seenPaths.Add(file.FullName) || !IsReadyForImport(file))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var id = OmniLibraryRomSystemRegistry.BuildStableGameId(system, identityPath);
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
                InstallPath = file.DirectoryName ?? string.Empty,
                ExecutablePath = file.FullName,
                ProviderGameId = identityPath,
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
            return true;
        }

        foreach (var source in BuildSources(store, root))
        {
            foreach (var (folderSystem, folder) in source.Folders)
            {
                var files = EnumerateFiles(folder).ToArray();
                var playlistMembers = PlaylistMembers(files);
                foreach (var path in files)
                {
                    if (playlistMembers.Contains(path) ||
                        OmniLibraryRomSystemRegistry.DetectSystem(folderSystem, path) is not { } system)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(source.Root, path);
                    var identityPath = string.IsNullOrEmpty(source.IdPrefix)
                        ? relativePath
                        : Path.Combine(source.IdPrefix, relativePath);
                    if (AddGame(system, path, identityPath) && !string.IsNullOrEmpty(source.IdPrefix))
                    {
                        orbitGames++;
                    }
                }
            }
        }

        if (store.ImportEmulatorLibraries)
        {
            // Game folders and game lists of installed emulators, imported in place.
            foreach (var source in OmniLibraryEmulatorGameLists.Discover())
            {
                foreach (var (system, path) in OmniLibraryEmulatorGameLists.EnumerateGames(
                             source,
                             directory => source.Recursive
                                 ? EnumerateFiles(directory)
                                 : EnumerateTopLevelFiles(directory),
                             PlaylistMembers))
                {
                    if (AddGame(system, path, $"{OmniLibraryRomSystemRegistry.EmulatorListSourcePrefix}|{path}"))
                    {
                        emulatorListGames++;
                    }
                }
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

        var systemCount = games
            .Select(game => game.PlatformId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var cache = store.Cache ??= new UnifySteamLibraryCache();
        cache.AccountName = "Local ROMs";
        cache.Games = games;
        cache.LastError = string.Empty;
        cache.StatusText = "Ready";
        cache.DetailText = games.Count == 0
            ? $"Put ROMs into the matching system folders in {root}."
            : $"{games.Count} ROM{(games.Count == 1 ? string.Empty : "s")} on {systemCount} system{(systemCount == 1 ? string.Empty : "s")}" +
              (orbitGames > 0 ? $", {orbitGames} from ORBIT's ROM folders" : string.Empty) +
              (emulatorListGames > 0 ? $", {emulatorListGames} from emulator game lists" : string.Empty) +
              ". Only file changes are processed.";
        cache.RefreshedAtUtc = DateTimeOffset.UtcNow;
        store.RemoteCatalogItemIds = games.Select(game => game.Id).ToList();
        store.RemoteCatalogSignature = ComputeCatalogSignature(games);
    }

    /// <summary>
    /// Queues the recommended (or chosen) emulator for every system that has
    /// ROMs but no ready emulator. Installs run in the background, one at a
    /// time, and wait while a game is running.
    /// </summary>
    public static void QueueAutomaticEmulatorInstalls(UnifySteamStoreConfiguration store)
    {
        if (!store.Enabled || !store.AutoInstallEmulators)
        {
            return;
        }

        var systemIds = (store.Cache?.Games ?? [])
            .Select(game => game.PlatformId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var systemId in systemIds)
        {
            try
            {
                if (!OmniLibraryRomSystemRegistry.TryGet(systemId, out var system))
                {
                    continue;
                }

                store.RomSystems.TryGetValue(system.Id, out var settings);
                var selection = OmniLibraryEmulatorDetector.Resolve(
                    system,
                    settings?.EmulatorId,
                    settings?.EmulatorPath);
                if (!selection.Ready && selection.InstallCandidate is { CanInstall: true } candidate)
                {
                    OmniLibraryEmulatorInstaller.Request(system.Id, candidate.Id, automatic: true);
                }
            }
            catch
            {
                // One system's emulator must never block the library refresh.
            }
        }
    }

    private static IEnumerable<RomSource> BuildSources(UnifySteamStoreConfiguration store, string root)
    {
        yield return new RomSource(
            root,
            string.Empty,
            OmniLibraryRomSystemRegistry.Supported
                .Select(system => (system, Path.Combine(root, system.FolderName)))
                .ToArray());

        if (!store.ImportOrbitRoms)
        {
            yield break;
        }

        var orbitRoot = OmniLibraryRomSystemRegistry.OrbitRomRootPath;
        var directories = OrbitSystemDirectories(orbitRoot, root);
        var folders = directories
            .Select(directory => (System: OmniLibraryRomSystemRegistry.FindByOrbitFolder(Path.GetFileName(directory)), Folder: directory))
            .Where(entry => entry.System is not null)
            .Select(entry => (entry.System!, entry.Folder))
            .ToArray();
        if (folders.Length > 0)
        {
            yield return new RomSource(orbitRoot, OmniLibraryRomSystemRegistry.OrbitSourcePrefix, folders);
        }
    }

    private static string[] OrbitSystemDirectories(string orbitRoot, string tfsRoot)
    {
        try
        {
            return Directory.Exists(orbitRoot) &&
                   !Path.GetFullPath(orbitRoot).TrimEnd('\\').Equals(tfsRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? Directory.GetDirectories(orbitRoot)
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Disc files listed in an .m3u playlist are imported once, through the
    /// playlist, instead of once per disc.
    /// </summary>
    private static ISet<string> PlaylistMembers(IReadOnlyList<string> files)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in files.Where(file => Path.GetExtension(file).Equals(".m3u", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var info = new FileInfo(playlist);
                if (info.Length > MaximumPlaylistBytes)
                {
                    continue;
                }

                var directory = info.DirectoryName ?? string.Empty;
                foreach (var line in File.ReadLines(playlist))
                {
                    var entry = line.Trim();
                    if (entry.Length == 0 || entry.StartsWith('#'))
                    {
                        continue;
                    }

                    members.Add(Path.GetFullPath(Path.Combine(directory, entry)));
                }
            }
            catch
            {
            }
        }

        return members;
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

    private static IEnumerable<string> EnumerateTopLevelFiles(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root).Select(Path.GetFullPath).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
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
                        MaxRecursionDepth = 8,
                    })
                .GetEnumerator();
            while (true)
            {
                string path;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    path = Path.GetFullPath(enumerator.Current);
                }
                catch
                {
                    break;
                }

                yield return path;
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
