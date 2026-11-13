using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SteamLoader.App.Infrastructure.Overture;

/// <summary>
/// TFS Overture backend: theme music for Big Picture game pages. Keeps the
/// playlists, finds and downloads themes (YouTube through yt-dlp, on request),
/// offers the user's own music files and serves the audio to Steam. Playback
/// itself runs in Big Picture (overture-core.js).
/// </summary>
/// <remarks>Feature idea from ThemeDeck for Decky (BrenticusMaximus); independent code.</remarks>
public sealed class OvertureService : IDisposable
{
    public static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".flac", ".webm"];

    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan LocalListLifetime = TimeSpan.FromSeconds(30);
    private const int MaxLocalFiles = 4000;

    private readonly OvertureSettingsStore _store;
    private readonly string _downloadDirectory;
    private readonly string _localDirectory;
    private readonly OvertureYoutubeTools _youtube;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, Task<string>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<OverturePlaylistSnapshot>> _autoFinds = new(StringComparer.Ordinal);
    private readonly object _localGate = new();
    private (DateTimeOffset At, string Signature, List<FileInfo> Files)? _localCache;

    public OvertureService(OvertureSettingsStore store, string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _downloadDirectory = Path.Combine(dataDirectory, "downloads");
        _localDirectory = Path.Combine(dataDirectory, "music");
        _youtube = new OvertureYoutubeTools(Path.Combine(dataDirectory, "tools"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public OvertureYoutubeTools Youtube => _youtube;

    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------

    public OvertureGlobalSnapshot GetSettings() => ToGlobal(_store.Load());

    public OvertureGlobalSnapshot SaveSettings(OvertureGlobalUpdate update)
    {
        var configuration = _store.Update(configuration =>
        {
            if (update.Enabled is { } enabled) configuration.Enabled = enabled;
            if (update.MasterVolume is { } volume) configuration.MasterVolume = volume;
            if (update.FadeSeconds is { } fade) configuration.FadeSeconds = fade;
            if (update.DelaySeconds is { } delay) configuration.DelaySeconds = delay;
            if (update.AutoFind is { } autoFind) configuration.AutoFind = autoFind;
            if (update.ShowNowPlaying is { } show) configuration.ShowNowPlaying = show;
            if (update.YieldToTrailers is { } yieldToTrailers) configuration.YieldToTrailers = yieldToTrailers;
            if (update.AmbientEnabled is { } ambient) configuration.AmbientEnabled = ambient;
            if (update.MusicFolders is { } folders) configuration.MusicFolders = folders;
        });
        InvalidateLocalFiles();
        return ToGlobal(configuration);
    }

    // ------------------------------------------------------------------
    // Playlists
    // ------------------------------------------------------------------

    public OverturePlaylistSnapshot GetPlaylist(string key, string? title, string? message = null)
    {
        var configuration = _store.Load();
        return ToPlaylist(configuration, key, CleanTitle(title), message);
    }

    public OverturePlaylistSnapshot SavePlaylist(string key, OverturePlaylistUpdate update, string? title)
    {
        var configuration = _store.Update(configuration =>
        {
            var playlist = GetOrCreate(configuration, key);
            if (update.ClearMode == true) playlist.Mode = null;
            if (update.Mode is { } mode) playlist.Mode = mode;
            if (update.Volume is { } volume) playlist.Volume = volume;
            if (update.StartSeconds is { } start) playlist.StartSeconds = start;
            if (update.Loop is { } loop) playlist.Loop = loop;
            if (update.TrackIds is { } order)
            {
                // Reordering only: the ids must be the playlist's own tracks.
                var current = playlist.TrackIds.ToHashSet(StringComparer.Ordinal);
                var reordered = order.Where(current.Contains).Distinct(StringComparer.Ordinal).ToList();
                reordered.AddRange(playlist.TrackIds.Where(id => !reordered.Contains(id, StringComparer.Ordinal)));
                playlist.TrackIds = reordered;
            }
        });
        return ToPlaylist(configuration, key, CleanTitle(title), null);
    }

    public OverturePlaylistSnapshot RemoveTrack(string key, string trackId, string? title)
    {
        var configuration = _store.Update(configuration =>
        {
            if (configuration.Playlists.TryGetValue(key, out var playlist))
            {
                playlist.TrackIds.RemoveAll(id => id == trackId);
                // An emptied automatic playlist must not be refilled automatically.
                if (playlist.TrackIds.Count == 0 && playlist.Source == "auto")
                {
                    playlist.Source = "manual";
                }
            }
        });
        return ToPlaylist(configuration, key, CleanTitle(title), null);
    }

    public OverturePlaylistSnapshot ResetPlaylist(string key, string? title)
    {
        var cleanTitle = CleanTitle(title);
        var configuration = _store.Update(configuration =>
        {
            configuration.Playlists.Remove(key);
            if (cleanTitle.Length > 0)
            {
                configuration.AutoFindMisses.Remove(NormalizeTitle(cleanTitle));
            }
        });
        return ToPlaylist(configuration, key, cleanTitle, null);
    }

    // ------------------------------------------------------------------
    // YouTube
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<OvertureSearchResult>> SearchYoutubeAsync(string term, CancellationToken cancellationToken)
    {
        await _youtube.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        return await _youtube.SearchAsync(term, 10, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Prefetch: themes for the recently played installed games
    // ------------------------------------------------------------------

    internal const int MaxPrefetchGames = 30;
    private static readonly TimeSpan PrefetchPause = TimeSpan.FromSeconds(2);
    private readonly object _prefetchGate = new();
    private Task? _prefetchTask;
    private OverturePrefetchStatus _prefetch = new(false, 0, 0, 0, null);

    public OverturePrefetchStatus GetPrefetchStatus()
    {
        lock (_prefetchGate)
        {
            return _prefetch;
        }
    }

    /// <summary>
    /// Finds themes for up to <see cref="MaxPrefetchGames"/> games one after
    /// another in the background, so their pages play right away. Games with
    /// music, silenced games and recent misses are skipped.
    /// </summary>
    public OverturePrefetchStatus StartPrefetch(IReadOnlyList<(uint AppId, string Title)> games)
    {
        lock (_prefetchGate)
        {
            if (_prefetchTask is { IsCompleted: false })
            {
                return _prefetch;
            }

            var configuration = _store.Load();
            var queue = games
                .Where(game => game.AppId > 0)
                .DistinctBy(game => game.AppId)
                .Take(MaxPrefetchGames)
                .Select(game => (game.AppId, Title: CleanTitle(game.Title)))
                .Where(game => CanAutoFind(configuration, game.AppId.ToString(CultureInfo.InvariantCulture), game.Title))
                .ToList();
            _prefetch = new OverturePrefetchStatus(queue.Count > 0, 0, queue.Count, 0, null);
            if (queue.Count > 0)
            {
                _prefetchTask = Task.Run(() => RunPrefetchAsync(queue));
            }

            return _prefetch;
        }
    }

    private async Task RunPrefetchAsync(List<(uint AppId, string Title)> queue)
    {
        var found = 0;
        string? message = null;
        for (var index = 0; index < queue.Count; index++)
        {
            var (appId, title) = queue[index];
            try
            {
                var configuration = _store.Load();
                if (!configuration.Enabled || !configuration.AutoFind)
                {
                    message = "Stopped: automatic themes were turned off.";
                    break;
                }

                var snapshot = await AutoFindAsync(appId, title, CancellationToken.None).ConfigureAwait(false);
                if (snapshot.Tracks.Count > 0) found++;
                if (!_youtube.IsInstalled && !_youtube.CanInstallAutomatically)
                {
                    message = _youtube.GetStatus().Message;
                    break;
                }
            }
            catch (Exception exception)
            {
                message = exception.Message;
            }

            lock (_prefetchGate)
            {
                _prefetch = new OverturePrefetchStatus(true, index + 1, queue.Count, found, message);
            }

            await Task.Delay(PrefetchPause).ConfigureAwait(false);
        }

        lock (_prefetchGate)
        {
            _prefetch = _prefetch with { Running = false, Found = found, Message = message };
        }
    }

    public async Task<OverturePlaylistSnapshot> AddYoutubeAsync(
        string key,
        OvertureSearchResult video,
        string? title,
        CancellationToken cancellationToken)
    {
        var track = await EnsureYoutubeTrackAsync(video, cancellationToken).ConfigureAwait(false);
        var configuration = _store.Update(configuration =>
        {
            configuration.Tracks[track.Id] = track;
            AppendTrack(configuration, key, track.Id, "manual");
        });
        return ToPlaylist(configuration, key, CleanTitle(title), null);
    }

    /// <summary>
    /// Fills an empty game playlist from YouTube: searches "<title> main theme"
    /// and only takes a result whose title names the game and looks like its
    /// theme (see <see cref="PickAutomatic"/>). Nothing found is remembered for
    /// a week so a page does not search again every time it opens.
    /// </summary>
    public Task<OverturePlaylistSnapshot> AutoFindAsync(uint appId, string? title, CancellationToken cancellationToken)
    {
        var key = appId.ToString(CultureInfo.InvariantCulture);
        return _autoFinds.GetOrAdd(key, _ => RunAutoFindAsync(key, CleanTitle(title)))
            .WaitAsync(cancellationToken);
    }

    private async Task<OverturePlaylistSnapshot> RunAutoFindAsync(string key, string title)
    {
        try
        {
            var configuration = _store.Load();
            if (!CanAutoFind(configuration, key, title))
            {
                return ToPlaylist(configuration, key, title, null);
            }

            var normalized = NormalizeTitle(title);
            IReadOnlyList<OvertureSearchResult> results;
            try
            {
                // The first automatic theme sets up YouTube support on its own.
                await _youtube.EnsureInstalledAsync(CancellationToken.None).ConfigureAwait(false);
                results = await _youtube.SearchAsync($"{title} main theme", 12, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return ToPlaylist(configuration, key, title, $"The automatic search failed: {exception.Message}");
            }

            var pick = PickAutomatic(title, results);
            if (pick is null)
            {
                configuration = _store.Update(value => value.AutoFindMisses[normalized] = _clock());
                return ToPlaylist(configuration, key, title, "No fitting theme was found automatically.");
            }

            OvertureTrack track;
            try
            {
                track = await EnsureYoutubeTrackAsync(pick, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return ToPlaylist(_store.Load(), key, title, $"The theme could not be downloaded: {exception.Message}");
            }

            configuration = _store.Update(value =>
            {
                value.Tracks[track.Id] = track;
                // The user may have chosen music while the search ran.
                if (!value.Playlists.TryGetValue(key, out var playlist) ||
                    (playlist.Mode is null && playlist.TrackIds.Count == 0 && playlist.Source is null))
                {
                    AppendTrack(value, key, track.Id, "auto");
                }
            });
            return ToPlaylist(configuration, key, title, null);
        }
        finally
        {
            _autoFinds.TryRemove(key, out _);
        }
    }

    private bool CanAutoFind(OvertureConfiguration configuration, string key, string title)
    {
        if (!configuration.Enabled || !configuration.AutoFind || !_youtube.CanInstallAutomatically ||
            key == OvertureConfiguration.AmbientKey || NormalizeTitle(title).Length < 2)
        {
            return false;
        }

        if (configuration.Playlists.TryGetValue(key, out var playlist) &&
            (playlist.Mode == "off" || playlist.TrackIds.Count > 0 || playlist.Source is not null))
        {
            return false;
        }

        return !configuration.AutoFindMisses.TryGetValue(NormalizeTitle(title), out var missedAt) ||
            _clock() - missedAt > MissRetryAfter;
    }

    private async Task<OvertureTrack> EnsureYoutubeTrackAsync(OvertureSearchResult video, CancellationToken cancellationToken)
    {
        if (!OvertureSettingsStore.IsValidVideoId(video.VideoId))
        {
            throw new ArgumentException("Invalid video id.");
        }

        var id = OvertureSettingsStore.YoutubeTrackId(video.VideoId);
        var existing = _store.Load().Tracks.GetValueOrDefault(id);
        if (existing?.File is { } file && File.Exists(file))
        {
            return existing;
        }

        var path = await _downloads.GetOrAdd(id, _ => DownloadOnceAsync(id, video.VideoId))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new OvertureTrack
        {
            Id = id,
            Kind = "youtube",
            Source = video.VideoId,
            File = path,
            Title = Truncate(video.Title, 200),
            Channel = video.Channel is null ? null : Truncate(video.Channel, 120),
            DurationSeconds = video.DurationSeconds,
            AddedAt = existing?.AddedAt ?? _clock(),
        };
    }

    private async Task<string> DownloadOnceAsync(string id, string videoId)
    {
        try
        {
            return await _youtube.DownloadAudioAsync(videoId, _downloadDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _downloads.TryRemove(id, out _);
        }
    }

    /// <summary>Best automatic theme among search results, or null when none is convincing.</summary>
    internal static OvertureSearchResult? PickAutomatic(string gameTitle, IReadOnlyList<OvertureSearchResult> results)
    {
        var titleTokens = NormalizeTitle(gameTitle).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (titleTokens.Length == 0)
        {
            return null;
        }

        string[] rejected =
        [
            "reaction", "review", "gameplay", "walkthrough", "trailer", "cover", "remix", "lyrics", "nightcore",
            "tutorial", "speedrun", "lets play", "8 bit", "8bit", "hour", "hours", "extended", "full album",
            "full soundtrack", "piano", "guitar", "orchestra version", "slowed", "reverb", "meme", "parody", "live"
        ];
        OvertureSearchResult? best = null;
        var bestScore = 0;
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (result.DurationSeconds is not { } seconds || seconds < 40 || seconds > 600)
            {
                continue;
            }

            var text = $" {NormalizeTitle(result.Title)} ";
            if (!titleTokens.All(token => text.Contains($" {token} ", StringComparison.Ordinal)))
            {
                continue;
            }

            if (rejected.Any(word => text.Contains($" {word} ", StringComparison.Ordinal)))
            {
                continue;
            }

            var score = 0;
            if (text.Contains(" main theme ", StringComparison.Ordinal)) score += 4;
            else if (text.Contains(" theme ", StringComparison.Ordinal)) score += 2;
            if (text.Contains(" main menu ", StringComparison.Ordinal) ||
                text.Contains(" title screen ", StringComparison.Ordinal) ||
                text.Contains(" menu music ", StringComparison.Ordinal) ||
                text.Contains(" menu theme ", StringComparison.Ordinal)) score += 3;
            if (text.Contains(" ost ", StringComparison.Ordinal) ||
                text.Contains(" soundtrack ", StringComparison.Ordinal) ||
                text.Contains(" original ", StringComparison.Ordinal)) score += 1;
            if (text.Contains(" official ", StringComparison.Ordinal)) score += 1;
            if (score < 2)
            {
                continue;
            }

            // YouTube's own order counts as a tie breaker.
            score = score * 10 + Math.Max(0, 9 - index);
            if (score > bestScore)
            {
                bestScore = score;
                best = result;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------
    // Local music
    // ------------------------------------------------------------------

    public IReadOnlyList<OvertureLocalFile> ListLocalFiles(string? term, int limit)
    {
        var configuration = _store.Load();
        var roots = EffectiveMusicFolders(configuration);
        var assigned = configuration.Tracks.Values
            .Where(track => track.Kind == "local")
            .Select(track => track.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = NormalizeTitle(term).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return EnumerateLocal(roots)
            .Where(file => tokens.Length == 0 || tokens.All(token => NormalizeTitle(file.FullName).Contains(token, StringComparison.Ordinal)))
            .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(file => new OvertureLocalFile(
                file.FullName,
                Path.GetFileNameWithoutExtension(file.Name),
                file.DirectoryName ?? string.Empty,
                file.Exists ? file.Length : 0,
                assigned.Contains(file.FullName)))
            .ToList();
    }

    public OverturePlaylistSnapshot AddLocal(string key, string path, string? title)
    {
        var configuration = _store.Load();
        var full = Path.GetFullPath(path);
        if (!IsAudioFile(full) || !File.Exists(full) ||
            !EffectiveMusicFolders(configuration).Any(root => OvertureYoutubeTools.IsInside(root, full)))
        {
            throw new ArgumentException("Only audio files inside the music folders can be added.");
        }

        var id = LocalTrackId(full);
        configuration = _store.Update(value =>
        {
            value.Tracks[id] = value.Tracks.GetValueOrDefault(id) ?? new OvertureTrack
            {
                Id = id,
                Kind = "local",
                Source = full,
                Title = Truncate(Path.GetFileNameWithoutExtension(full), 200),
                AddedAt = _clock(),
            };
            AppendTrack(value, key, id, "manual");
        });
        return ToPlaylist(configuration, key, CleanTitle(title), null);
    }

    internal IReadOnlyList<string> EffectiveMusicFolders(OvertureConfiguration configuration)
    {
        var folders = configuration.MusicFolders.Count > 0
            ? configuration.MusicFolders.ToList()
            : [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)];
        try
        {
            Directory.CreateDirectory(_localDirectory);
        }
        catch
        {
        }

        folders.Add(_localDirectory);
        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<FileInfo> EnumerateLocal(IReadOnlyList<string> roots)
    {
        var signature = string.Join('|', roots);
        lock (_localGate)
        {
            if (_localCache is { } cache && cache.Signature == signature && _clock() - cache.At < LocalListLifetime)
            {
                return cache.Files;
            }
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 5,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new List<FileInfo>();
        foreach (var root in roots)
        {
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    if (IsAudioFile(file.Name))
                    {
                        files.Add(file);
                        if (files.Count >= MaxLocalFiles) break;
                    }
                }
            }
            catch
            {
                // An unplugged drive or a removed folder only hides its files.
            }

            if (files.Count >= MaxLocalFiles) break;
        }

        lock (_localGate)
        {
            _localCache = (_clock(), signature, files);
        }

        return files;
    }

    private void InvalidateLocalFiles()
    {
        lock (_localGate)
        {
            _localCache = null;
        }
    }

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static string LocalTrackId(string fullPath) =>
        "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..16].ToLowerInvariant();

    // ------------------------------------------------------------------
    // Audio and storage
    // ------------------------------------------------------------------

    /// <summary>The audio file of a known track, or null. Only files TFS Overture knows are served.</summary>
    public string? ResolveAudioFile(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !_store.Load().Tracks.TryGetValue(trackId, out var track))
        {
            return null;
        }

        var path = track.Kind == "youtube" ? track.File : track.Source;
        if (string.IsNullOrWhiteSpace(path) || !IsAudioFile(path) || !File.Exists(path))
        {
            return null;
        }

        if (track.Kind == "youtube" && !OvertureYoutubeTools.IsInside(_downloadDirectory, path))
        {
            return null;
        }

        return path;
    }

    public OvertureStorageSnapshot GetStorage()
    {
        var configuration = _store.Load();
        var counts = CountAssignments(configuration);
        var entries = configuration.Tracks.Values
            .Where(track => track.Kind == "youtube" && track.File is not null && File.Exists(track.File))
            .Select(track => new OvertureStorageEntry(
                track.Id,
                track.Title,
                new FileInfo(track.File!).Length,
                counts.GetValueOrDefault(track.Id)))
            .ToList();
        var known = entries.Select(entry => configuration.Tracks[entry.TrackId].File!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strays = Directory.Exists(_downloadDirectory)
            ? new DirectoryInfo(_downloadDirectory).EnumerateFiles().Where(file => !known.Contains(file.FullName)).ToList()
            : [];
        var unused = entries.Where(entry => entry.AssignedCount == 0).ToList();
        return new OvertureStorageSnapshot(
            entries.Sum(entry => entry.SizeBytes) + strays.Sum(file => file.Length),
            entries.Count + strays.Count,
            unused.Sum(entry => entry.SizeBytes) + strays.Sum(file => file.Length),
            unused.Count + strays.Count,
            entries.OrderByDescending(entry => entry.SizeBytes).Take(20).ToList());
    }

    /// <summary>
    /// Deletes downloaded themes no playlist uses any more, plus leftovers of
    /// interrupted downloads. Only TFS Overture's own download folder is touched;
    /// the user's local music is never deleted.
    /// </summary>
    public OvertureStorageSnapshot DeleteUnusedDownloads()
    {
        if (!_downloads.IsEmpty || !_autoFinds.IsEmpty)
        {
            throw new InvalidOperationException("Wait until the running download has finished.");
        }

        var removedFiles = new List<string>();
        _store.Update(configuration =>
        {
            var counts = CountAssignments(configuration);
            foreach (var track in configuration.Tracks.Values.Where(track => track.Kind == "youtube").ToList())
            {
                if (counts.GetValueOrDefault(track.Id) > 0)
                {
                    continue;
                }

                configuration.Tracks.Remove(track.Id);
                if (track.File is { } file && OvertureYoutubeTools.IsInside(_downloadDirectory, file))
                {
                    removedFiles.Add(file);
                }
            }

            var keep = configuration.Tracks.Values
                .Select(track => track.File)
                .Where(file => file is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_downloadDirectory))
            {
                removedFiles.AddRange(Directory.EnumerateFiles(_downloadDirectory).Where(file => !keep.Contains(file)));
            }
        });

        foreach (var file in removedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }

        return GetStorage();
    }

    private static Dictionary<string, int> CountAssignments(OvertureConfiguration configuration) =>
        configuration.Playlists.Values
            .SelectMany(playlist => playlist.TrackIds)
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    // ------------------------------------------------------------------
    // Snapshots
    // ------------------------------------------------------------------

    private OvertureGlobalSnapshot ToGlobal(OvertureConfiguration configuration)
    {
        var silent = new List<uint>();
        var withMusic = new List<uint>();
        foreach (var (key, playlist) in configuration.Playlists)
        {
            if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            {
                continue;
            }

            if (playlist.Mode == "off") silent.Add(appId);
            else if (playlist.TrackIds.Count > 0) withMusic.Add(appId);
        }

        return new OvertureGlobalSnapshot(
            configuration.Enabled,
            configuration.MasterVolume,
            configuration.FadeSeconds,
            configuration.DelaySeconds,
            configuration.AutoFind,
            configuration.ShowNowPlaying,
            configuration.YieldToTrailers,
            configuration.AmbientEnabled,
            configuration.MusicFolders,
            EffectiveMusicFolders(configuration),
            silent,
            withMusic,
            _youtube.GetStatus(),
            GetPrefetchStatus());
    }

    private OverturePlaylistSnapshot ToPlaylist(OvertureConfiguration configuration, string key, string title, string? message)
    {
        configuration.Playlists.TryGetValue(key, out var playlist);
        var tracks = (playlist?.TrackIds ?? [])
            .Select(id => configuration.Tracks.GetValueOrDefault(id))
            .Where(track => track is not null)
            .Select(track => new OvertureTrackSnapshot(
                track!.Id,
                track.Kind,
                track.Title,
                track.Channel,
                track.DurationSeconds,
                ResolveAudioFile(track.Id) is null,
                $"api/overture/audio?track={Uri.EscapeDataString(track.Id)}"))
            .ToList();
        var silent = playlist?.Mode == "off";
        var isAmbient = key == OvertureConfiguration.AmbientKey;
        uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId);
        return new OverturePlaylistSnapshot(
            key,
            appId,
            title.Length > 0 ? title : null,
            configuration.Enabled && !silent && (!isAmbient || configuration.AmbientEnabled),
            silent,
            tracks,
            playlist?.Volume ?? 100,
            playlist?.StartSeconds ?? 0,
            playlist?.Loop ?? true,
            playlist?.Source,
            tracks.Count == 0 && CanAutoFind(configuration, key, title),
            message,
            ToGlobal(configuration));
    }

    private static OverturePlaylist GetOrCreate(OvertureConfiguration configuration, string key)
    {
        if (!configuration.Playlists.TryGetValue(key, out var playlist))
        {
            playlist = new OverturePlaylist();
            configuration.Playlists[key] = playlist;
        }

        return playlist;
    }

    private void AppendTrack(OvertureConfiguration configuration, string key, string trackId, string source)
    {
        var playlist = GetOrCreate(configuration, key);
        if (!playlist.TrackIds.Contains(trackId, StringComparer.Ordinal))
        {
            playlist.TrackIds.Add(trackId);
        }

        // Choosing music for a silenced game turns it back on.
        playlist.Mode = null;
        playlist.Source = playlist.Source == "manual" ? "manual" : source;
        playlist.AssignedAt = _clock();
    }

    internal static string CleanTitle(string? title)
    {
        var text = new string((title ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return text.Length > 160 ? text[..160] : text;
    }

    /// <summary>Lower case letters and digits separated by single spaces (™, ®, punctuation removed).</summary>
    internal static string NormalizeTitle(string? text)
    {
        var builder = new StringBuilder();
        var lastWasSpace = true;
        foreach (var character in (text ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark || character == '\'' || character == '’')
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string text, int length) =>
        text.Length > length ? text[..length] : text;

    public void Dispose() => _youtube.Dispose();
}
