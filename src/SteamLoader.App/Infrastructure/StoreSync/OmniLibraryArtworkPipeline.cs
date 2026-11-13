using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Provider-neutral OmniLibrary artwork pipeline. Every integration uses the
/// same local-first source order before optional public and community sources.
/// </summary>
internal sealed class OmniLibraryArtworkPipeline
{
    public const string BuiltInApiKey = "96b06c7e805c21ee48af894587118c4c";
    private const long MaximumArtworkBytes = 32L * 1024 * 1024;
    private const int MinimumArtworkBytes = 128;
    private const int MaximumArtworkValidationEntries = 8192;
    private static readonly ConcurrentDictionary<string, ArtworkValidationEntry>
        ArtworkValidationCache = new(StringComparer.OrdinalIgnoreCase);
    private static long _lastStagingCleanupUtcTicks;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ArtworkSlot[] ArtworkSlots =
    [
        new(
            SlotName: "library capsule",
            FileStemBuilder: gridId => gridId,
            PreferredWidth: 920,
            PreferredHeight: 430,
            Orientation: OmniLibraryArtworkOrientation.Capsule,
            LocalKind: OmniLibraryArtworkSlotKind.LibraryCapsule,
            SupportsBadge: true),
        new(
            SlotName: "portrait",
            FileStemBuilder: gridId => $"{gridId}p",
            PreferredWidth: 600,
            PreferredHeight: 900,
            Orientation: OmniLibraryArtworkOrientation.Portrait,
            LocalKind: OmniLibraryArtworkSlotKind.Portrait,
            SupportsBadge: true),
        new(
            SlotName: "hero",
            FileStemBuilder: gridId => $"{gridId}_hero",
            PreferredWidth: 1920,
            PreferredHeight: 620,
            Orientation: OmniLibraryArtworkOrientation.Hero,
            LocalKind: OmniLibraryArtworkSlotKind.Hero,
            SupportsBadge: true),
        new(
            SlotName: "logo",
            FileStemBuilder: gridId => $"{gridId}_logo",
            PreferredWidth: null,
            PreferredHeight: null,
            Orientation: OmniLibraryArtworkOrientation.Logo,
            LocalKind: OmniLibraryArtworkSlotKind.Logo,
            SupportsBadge: false),
        new(
            SlotName: "icon",
            FileStemBuilder: gridId => $"{gridId}-icon",
            PreferredWidth: 256,
            PreferredHeight: 256,
            Orientation: OmniLibraryArtworkOrientation.Icon,
            LocalKind: OmniLibraryArtworkSlotKind.Icon,
            SupportsBadge: false),
    ];

    /// <summary>
    /// Classic Store Sync shortcuts use the same automatic pipeline as
    /// OmniLibrary, so every TFS surface follows ORBIT's source order.
    /// </summary>
    public Task<StoreSyncArtworkSummary> DownloadAsync(
        string gridDirectory,
        IReadOnlyList<StoreSyncArtworkTarget> targets,
        string apiKey,
        bool preferAnimatedArtwork,
        CancellationToken cancellationToken)
    {
        _ = preferAnimatedArtwork;
        return DownloadLocalFirstAsync(gridDirectory, targets, apiKey, cancellationToken);
    }

    /// <summary>
    /// Fills every Steam library artwork slot with ORBIT's automatic artwork
    /// pipeline (ORBIT src/main/imageCache.ts). Per slot, sources are tried in
    /// ORBIT's order and every image is checked for size and aspect ratio. A
    /// high-quality hit ends the search for that slot; a low-quality hit is
    /// written at once as a visible fallback while later sources may still
    /// replace it with a better one. Files already in the grid folder (manual
    /// TFS Artworks picks or earlier runs) are never replaced.
    /// </summary>
    public async Task<StoreSyncArtworkSummary> DownloadLocalFirstAsync(
        string gridDirectory,
        IReadOnlyList<StoreSyncArtworkTarget> targets,
        string apiKey,
        CancellationToken cancellationToken,
        Action<int, int>? progress = null)
    {
        if (targets.Count == 0)
        {
            return new StoreSyncArtworkSummary(0, 0, []);
        }

        Directory.CreateDirectory(gridDirectory);
        CleanupStaleArtworkStagingFiles(gridDirectory);
        using var steamClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        steamClient.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsForSteam-OmniLibrary/1.0");

        using var steamGridDbClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            steamGridDbClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var localArtworkResolver = new OmniLibraryLocalArtworkResolver();
        var updatedTitleIds = new List<string>();
        var updatedFileCount = 0;
        var failures = new List<string>();

        var uniqueTargets = targets
            .GroupBy(target => target.AppId)
            .Select(group => group
                .OrderByDescending(target => target.ForceReload)
                .First())
            .ToArray();
        var completedTargetCount = 0;

        // Deliberately sequential per store: images appear one title at a time without a
        // large first-sync bandwidth or CPU spike in Big Picture.
        foreach (var target in uniqueTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updatedForTitle = 0;
            var gridId = SteamShortcutIds.BuildGridId(target.AppId);
            var workingGridDirectory = gridDirectory;
            string? reloadStagingDirectory = null;
            try
            {
                if (target.ForceReload)
                {
                    reloadStagingDirectory = Path.Combine(
                        gridDirectory,
                        $".tfs-artwork-reload-{gridId}-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(reloadStagingDirectory);
                    workingGridDirectory = reloadStagingDirectory;
                }

                var session = new ArtworkSlotSession(workingGridDirectory, gridId, target.StoreId);
                try
                {
                    if (session.HasPendingSlots)
                    {
                        await FillArtworkSetAsync(
                            session,
                            target,
                            localArtworkResolver,
                            steamClient,
                            string.IsNullOrWhiteSpace(apiKey) ? null : steamGridDbClient,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    updatedForTitle += session.UpdatedFileCount;
                }

                // Steam shows an empty tile for a missing slot, so every slot
                // gets a file. Only after all sources failed may a missing slot
                // be derived from a real asset of the same game, plus a
                // generated title logo (TFS-only; ORBIT shows a placeholder).
                updatedForTitle += EnsureCompleteArtworkSetFromExistingAssets(
                    workingGridDirectory,
                    gridId,
                    target.Title);

                if (target.ForceReload)
                {
                    updatedForTitle = HasCompleteArtworkSet(
                            workingGridDirectory,
                            target.AppId)
                        ? PromoteReloadedArtworkSet(
                            workingGridDirectory,
                            gridDirectory,
                            gridId)
                        : 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One missing/broken source must not stop the progressive queue.
                AddArtworkFailure(failures, target, exception);
            }
            finally
            {
                DeleteArtworkReloadStagingDirectory(reloadStagingDirectory);
            }

            if (updatedForTitle > 0)
            {
                updatedFileCount += updatedForTitle;
                updatedTitleIds.Add(target.TitleId);
            }

            completedTargetCount++;
            progress?.Invoke(completedTargetCount, uniqueTargets.Length);
        }

        return new StoreSyncArtworkSummary(
            updatedTitleIds.Count,
            updatedFileCount,
            updatedTitleIds,
            failures.Count,
            failures);
    }

    /// <summary>ORBIT's source order for one game. Each stage only touches unfinished slots.</summary>
    private static async Task FillArtworkSetAsync(
        ArtworkSlotSession session,
        StoreSyncArtworkTarget target,
        OmniLibraryLocalArtworkResolver resolver,
        HttpClient httpClient,
        HttpClient? steamGridDbClient,
        CancellationToken cancellationToken)
    {
        var isRom = target.StoreId.Equals(
            OmniLibraryRomSystemRegistry.StoreId,
            StringComparison.OrdinalIgnoreCase);

        // Stage 1: provider files already on this PC (catalog file references,
        // ROM sidecars, bounded install artwork) before any network request.
        var providerLocal = resolver.ResolveProviderLocal(target);
        await ImportLocalArtworkSetAsync(
            session,
            httpClient,
            providerLocal,
            crossOrientation: false,
            cancellationToken).ConfigureAwait(false);

        // Stage 2: provider metadata online. Catalog URLs and the exact-hash
        // RetroAchievements record come first, then the installed Steam copy
        // with the same canonical title, then cross-orientation provider
        // fallbacks, and for ROMs GameTDB's official scans and libretro.
        await DownloadRemoteStoreArtworkSetAsync(
            session,
            httpClient,
            target,
            crossOrientation: false,
            cancellationToken).ConfigureAwait(false);

        if (session.HasPendingSlots &&
            isRom &&
            !string.IsNullOrWhiteSpace(target.RomPath) &&
            !string.IsNullOrWhiteSpace(target.RomSystemId) &&
            !string.IsNullOrWhiteSpace(target.RetroAchievementsApiKey))
        {
            try
            {
                await DownloadRetroAchievementsArtworkSetAsync(
                    session,
                    httpClient,
                    target,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // RetroAchievements is optional; later sources stay independent.
            }
        }

        if (session.HasPendingSlots)
        {
            await ImportSteamLibraryMatchAsync(
                session,
                httpClient,
                resolver.ResolveSteamLibraryMatch(target),
                cancellationToken).ConfigureAwait(false);
        }

        await ImportLocalArtworkSetAsync(
            session,
            httpClient,
            providerLocal,
            crossOrientation: true,
            cancellationToken).ConfigureAwait(false);
        await DownloadRemoteStoreArtworkSetAsync(
            session,
            httpClient,
            target,
            crossOrientation: true,
            cancellationToken).ConfigureAwait(false);

        if (isRom)
        {
            await DownloadGameTdbArtworkAsync(session, httpClient, target, cancellationToken)
                .ConfigureAwait(false);
            await DownloadLibretroArtworkAsync(session, httpClient, target, cancellationToken)
                .ConfigureAwait(false);
        }

        // Stage 3: the exact public Steam Store search is free and needs no
        // key. It applies to PC stores only, never to ROMs.
        if (!isRom)
        {
            await DownloadPublicSteamArtworkSetAsync(session, httpClient, target, cancellationToken)
                .ConfigureAwait(false);
        }

        // Stage 4: SteamGridDB is intentionally last. A missing key, outage,
        // or rate limit can never block the earlier sources.
        if (steamGridDbClient is not null)
        {
            try
            {
                await DownloadArtworkSetAsync(
                    session,
                    httpClient,
                    steamGridDbClient,
                    target,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // SteamGridDB is optional.
            }
        }

        // ORBIT's icon of last resort: the game's own executable icon.
        var iconState = session.Pending(OmniLibraryArtworkOrientation.Icon).FirstOrDefault();
        if (iconState is { HasFallback: false } &&
            TryExtractExecutableIcon(target.LocalExecutablePath, out var iconPath))
        {
            try
            {
                await OfferArtworkAsync(
                    session,
                    iconState,
                    new DownloadedArtworkFile(iconPath, ".png"),
                    default,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DeleteFileIfExists(iconPath);
            }
        }
    }

    /// <summary>
    /// Validates one candidate for one slot (ORBIT acceptValidated). Strictly
    /// matching images keep their real proportions; explicit provider
    /// fallbacks are cropped (or contained for odd box art) into the slot.
    /// </summary>
    private static async Task<ArtworkOfferResult> OfferArtworkAsync(
        ArtworkSlotSession session,
        SlotState state,
        DownloadedArtworkFile file,
        ArtworkCandidateOptions options,
        CancellationToken cancellationToken)
    {
        if (state.Final)
        {
            return ArtworkOfferResult.Final;
        }

        var orientation = state.Slot.Orientation;
        DownloadedArtworkFile? cropped = null;
        try
        {
            var candidate = file;
            if (options.CropFrontCover is { } frontCoverStart)
            {
                cropped = CropFrontCover(file.TempPath, frontCoverStart);
                if (cropped is null)
                {
                    return ArtworkOfferResult.Rejected;
                }
                candidate = cropped;
            }

            if (!TryReadImageSize(candidate.TempPath, out var width, out var height))
            {
                return ArtworkOfferResult.Rejected;
            }

            var strict = OmniLibraryArtworkRules.MatchesOrientation(width, height, orientation);
            if (!strict &&
                !(options.AllowFallback &&
                  OmniLibraryArtworkRules.IsUsableFallback(width, height, orientation)))
            {
                return ArtworkOfferResult.Rejected;
            }

            // Official retail scans outrank community grids even below 600x900.
            var quality = options.OfficialScan && width >= 300 && height >= 450
                ? OmniLibraryArtworkQuality.High
                : OmniLibraryArtworkRules.AutomaticQuality(width, height, orientation);
            var score = OmniLibraryArtworkRules.AutomaticScore(width, height, orientation);
            var final = quality == OmniLibraryArtworkQuality.High && !options.ForceFallback;
            if (!final &&
                state.HasFallback &&
                !((quality == OmniLibraryArtworkQuality.High &&
                   state.FallbackQuality != OmniLibraryArtworkQuality.High) ||
                  (quality == state.FallbackQuality && score > state.FallbackScore)))
            {
                return ArtworkOfferResult.Fallback;
            }

            var fileStem = state.Slot.FileStemBuilder(session.GridId);
            var promoted = strict
                ? PromoteValidatedArtwork(session.GridDirectory, fileStem, candidate, orientation, width, height)
                : options.ContainFallback
                    ? PromoteContainedArtwork(session.GridDirectory, fileStem, candidate, state.Slot)
                    : PromoteStoreFallbackArtwork(session.GridDirectory, fileStem, candidate, state.Slot);
            if (!promoted)
            {
                return ArtworkOfferResult.Rejected;
            }

            session.UpdatedFileCount++;
            await TryApplyStoreBadgeAsync(
                session.GridDirectory,
                session.GridId,
                state.Slot,
                session.StoreId,
                cancellationToken).ConfigureAwait(false);
            if (final)
            {
                state.Final = true;
                return ArtworkOfferResult.Final;
            }

            // A usable image appears immediately while later sources keep
            // searching for a high-quality replacement.
            state.HasFallback = true;
            state.FallbackQuality = quality;
            state.FallbackScore = score;
            return ArtworkOfferResult.Fallback;
        }
        finally
        {
            if (cropped is not null)
            {
                DeleteFileIfExists(cropped.TempPath);
            }
        }
    }

    private static async Task<bool> TryCandidateUrlsAsync(
        ArtworkSlotSession session,
        SlotState state,
        HttpClient httpClient,
        IEnumerable<string> urls,
        ArtworkCandidateOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var url in urls
                     .Where(url => !string.IsNullOrWhiteSpace(url))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (state.Final)
            {
                return true;
            }

            var downloaded = await DownloadCandidateAsync(httpClient, url, cancellationToken)
                .ConfigureAwait(false);
            if (downloaded.State != ArtworkAttemptState.Success)
            {
                continue;
            }

            try
            {
                if (await OfferArtworkAsync(
                        session,
                        state,
                        downloaded.Value!,
                        options,
                        cancellationToken).ConfigureAwait(false) == ArtworkOfferResult.Final)
                {
                    return true;
                }
            }
            finally
            {
                DeleteFileIfExists(downloaded.Value!.TempPath);
            }
        }

        return state.Final;
    }

    /// <summary>
    /// Bounded download of one image (ORBIT downloadValidated): local files
    /// are copied, remote hosts go through the per-host circuit breaker, and
    /// server errors count as an outage instead of a missing asset.
    /// </summary>
    private static async Task<ArtworkAttempt<DownloadedArtworkFile>> DownloadCandidateAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        if (OmniLibraryLocalArtworkResolver.TryResolveLocalReference(url, out var localPath))
        {
            var local = await DownloadAssetToTemporaryFileAsync(
                httpClient,
                new Uri(localPath).AbsoluteUri,
                cancellationToken).ConfigureAwait(false);
            return local is null
                ? ArtworkAttempt<DownloadedArtworkFile>.Missing
                : ArtworkAttempt<DownloadedArtworkFile>.Success(local);
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ArtworkAttempt<DownloadedArtworkFile>.Missing;
        }

        return await OmniLibraryArtworkDiscovery.RunAsync(
            $"artwork:{uri.Host.ToLowerInvariant()}",
            async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                string? tempPath = null;
                try
                {
                    using var response = await httpClient.GetAsync(
                        uri,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return OmniLibraryArtworkRules.IsTransientStatus((int)response.StatusCode)
                            ? ArtworkAttempt<DownloadedArtworkFile>.Unavailable
                            : ArtworkAttempt<DownloadedArtworkFile>.Missing;
                    }

                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!IsSupportedArtworkResponse(mediaType, uri) ||
                        response.Content.Headers.ContentLength is > MaximumArtworkBytes)
                    {
                        return ArtworkAttempt<DownloadedArtworkFile>.Missing;
                    }

                    var extension = ResolveFileExtension(uri.AbsoluteUri, mediaType) ?? ".jpg";
                    tempPath = Path.Combine(
                        Path.GetTempPath(),
                        $"steamloader-grid-{Guid.NewGuid():N}{extension}");
                    await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token)
                                     .ConfigureAwait(false))
                    await using (var output = File.Create(tempPath))
                    {
                        await CopyArtworkWithLimitAsync(input, output, timeout.Token).ConfigureAwait(false);
                        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                    }

                    if (!IsUsableArtworkFile(tempPath))
                    {
                        DeleteFileIfExists(tempPath);
                        return ArtworkAttempt<DownloadedArtworkFile>.Missing;
                    }

                    return ArtworkAttempt<DownloadedArtworkFile>.Success(
                        new DownloadedArtworkFile(tempPath, extension));
                }
                catch (InvalidOperationException)
                {
                    // Over the size limit: a bad asset, not a provider outage.
                    if (tempPath is not null)
                    {
                        DeleteFileIfExists(tempPath);
                    }
                    return ArtworkAttempt<DownloadedArtworkFile>.Missing;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (tempPath is not null)
                    {
                        DeleteFileIfExists(tempPath);
                    }
                    throw new TimeoutException($"Artwork download timed out: {uri.Host}");
                }
                catch
                {
                    if (tempPath is not null)
                    {
                        DeleteFileIfExists(tempPath);
                    }
                    throw;
                }
            }).ConfigureAwait(false);
    }

    private static bool IsSupportedArtworkResponse(string? mediaType, Uri uri)
    {
        var normalized = mediaType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("image/", StringComparison.Ordinal))
        {
            return true;
        }
        // Some official CDNs serve valid images as generic binary data. Only an
        // explicit image extension qualifies; the decoder still validates it.
        return normalized is "application/octet-stream" or "binary/octet-stream" &&
               Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".ico" or ".jpeg" or ".jpg" or ".png" or ".webp";
    }

    private static bool TryReadImageSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            width = image.Width;
            height = image.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Keeps only the front of a full case scan, starting at this width fraction.</summary>
    private static DownloadedArtworkFile? CropFrontCover(string path, double startFraction)
    {
        if (startFraction is <= 0 or >= 1)
        {
            return null;
        }

        var outputPath = Path.Combine(Path.GetTempPath(), $"steamloader-grid-front-{Guid.NewGuid():N}.png");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var source = Image.FromStream(stream);
            // Full case scans are clearly landscape; anything else is not the expected layout.
            if ((double)source.Width / source.Height < 1.3)
            {
                return null;
            }

            var x = (int)Math.Round(source.Width * startFraction);
            using var front = new Bitmap(source.Width - x, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(front))
            {
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, front.Width, front.Height),
                    new Rectangle(x, 0, front.Width, front.Height),
                    GraphicsUnit.Pixel);
            }
            front.Save(outputPath, ImageFormat.Png);
            return new DownloadedArtworkFile(outputPath, ".png");
        }
        catch
        {
            DeleteFileIfExists(outputPath);
            return null;
        }
    }

    /// <summary>
    /// Writes a validated image at its real proportions, only downscaled to
    /// ORBIT's display cache size so Steam never decodes oversized bitmaps.
    /// </summary>
    private static bool PromoteValidatedArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset,
        OmniLibraryArtworkOrientation orientation,
        int width,
        int height)
    {
        var (limitWidth, limitHeight) = OmniLibraryArtworkRules.DisplayCacheLimit(orientation);
        var scale = Math.Min(1d, Math.Min((double)limitWidth / width, (double)limitHeight / height));
        if (scale >= 1d || asset.Extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }

        var preserveTransparency =
            orientation is OmniLibraryArtworkOrientation.Logo or OmniLibraryArtworkOrientation.Icon ||
            asset.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            asset.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        var outputExtension = preserveTransparency ? ".png" : ".jpg";
        var resizedPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-grid-display-{Guid.NewGuid():N}{outputExtension}");
        try
        {
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
            using (var input = new FileStream(asset.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var source = Image.FromStream(input))
            using (var output = new Bitmap(
                       targetWidth,
                       targetHeight,
                       preserveTransparency ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
                }

                if (preserveTransparency)
                {
                    output.Save(resizedPath, ImageFormat.Png);
                }
                else
                {
                    SaveJpeg(output, resizedPath, 88);
                }
            }

            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(resizedPath, outputExtension));
        }
        catch
        {
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }
        finally
        {
            DeleteFileIfExists(resizedPath);
        }
    }

    /// <summary>
    /// Box art outside the slot's proportions (ORBIT "contain"): the whole
    /// cover stays visible above a soft, darkened copy of itself instead of
    /// being destructively cropped.
    /// </summary>
    private static bool PromoteContainedArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset,
        ArtworkSlot slot)
    {
        if (!slot.PreferredWidth.HasValue || !slot.PreferredHeight.HasValue)
        {
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-grid-contain-{Guid.NewGuid():N}.jpg");
        try
        {
            var targetWidth = slot.PreferredWidth.Value;
            var targetHeight = slot.PreferredHeight.Value;
            using (var input = new FileStream(asset.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var source = Image.FromStream(input))
            using (var output = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.Black);
                    var cover = Math.Max(
                        (double)targetWidth / source.Width,
                        (double)targetHeight / source.Height);
                    const int blurFactor = 24;
                    using (var small = new Bitmap(
                               Math.Max(1, targetWidth / blurFactor),
                               Math.Max(1, targetHeight / blurFactor),
                               PixelFormat.Format24bppRgb))
                    {
                        using (var smallGraphics = Graphics.FromImage(small))
                        {
                            smallGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            var coverWidth = source.Width * cover / blurFactor;
                            var coverHeight = source.Height * cover / blurFactor;
                            smallGraphics.DrawImage(
                                source,
                                (float)((small.Width - coverWidth) / 2),
                                (float)((small.Height - coverHeight) / 2),
                                (float)coverWidth,
                                (float)coverHeight);
                        }
                        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(small, new Rectangle(0, 0, targetWidth, targetHeight));
                    }

                    using (var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                    {
                        graphics.FillRectangle(shade, 0, 0, targetWidth, targetHeight);
                    }

                    var fit = Math.Min(
                        (double)targetWidth / source.Width,
                        (double)targetHeight / source.Height);
                    var fitWidth = Math.Max(1, (int)Math.Round(source.Width * fit));
                    var fitHeight = Math.Max(1, (int)Math.Round(source.Height * fit));
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(
                        source,
                        (targetWidth - fitWidth) / 2,
                        (targetHeight - fitHeight) / 2,
                        fitWidth,
                        fitHeight);
                }

                SaveJpeg(output, outputPath, 90);
            }

            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(outputPath, ".jpg"));
        }
        catch
        {
            return PromoteStoreFallbackArtwork(gridDirectory, fileStem, asset, slot);
        }
        finally
        {
            DeleteFileIfExists(outputPath);
        }
    }

    private static void SaveJpeg(Image image, string path, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
        {
            image.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        image.Save(path, encoder, parameters);
    }

    private enum ArtworkOfferResult
    {
        Rejected,
        Fallback,
        Final,
    }

    private readonly record struct ArtworkCandidateOptions(
        bool AllowFallback = false,
        bool ContainFallback = false,
        bool ForceFallback = false,
        bool OfficialScan = false,
        double? CropFrontCover = null);

    private sealed class ArtworkSlotSession
    {
        public ArtworkSlotSession(string gridDirectory, string gridId, string storeId)
        {
            GridDirectory = gridDirectory;
            GridId = gridId;
            StoreId = storeId;
            // An existing file is the user's or an earlier run's choice.
            States = ArtworkSlots
                .Where(slot => !HasArtworkVariant(gridDirectory, slot.FileStemBuilder(gridId)))
                .Select(slot => new SlotState(slot))
                .ToArray();
        }

        public string GridDirectory { get; }
        public string GridId { get; }
        public string StoreId { get; }
        public IReadOnlyList<SlotState> States { get; }
        public int UpdatedFileCount { get; set; }
        public bool HasPendingSlots => States.Any(state => !state.Final);

        public SlotState[] Pending(params OmniLibraryArtworkOrientation[] orientations) =>
            States
                .Where(state => !state.Final &&
                                (orientations.Length == 0 || orientations.Contains(state.Slot.Orientation)))
                .ToArray();
    }

    private sealed class SlotState(ArtworkSlot slot)
    {
        public ArtworkSlot Slot { get; } = slot;
        public bool Final { get; set; }
        public bool HasFallback { get; set; }
        public OmniLibraryArtworkQuality FallbackQuality { get; set; }
        public double FallbackScore { get; set; }
    }

    private static void AddArtworkFailure(
        ICollection<string> failures,
        StoreSyncArtworkTarget target,
        Exception exception)
    {
        if (failures.Count >= 24)
        {
            return;
        }

        var message = Regex.Replace(exception.Message ?? string.Empty, "\\s+", " ").Trim();
        if (message.Length > 240)
        {
            message = message[..240];
        }
        failures.Add(
            $"{target.StoreId}/{target.Title}: {exception.GetType().Name}" +
            (string.IsNullOrWhiteSpace(message) ? string.Empty : $" - {message}"));
    }

    /// <summary>
    /// Local provider files (ORBIT localProviderMetadataCandidates). The
    /// cross-orientation pass lets a portrait fall back to the provider's wide
    /// art; a wide slot never silently adopts a portrait cover.
    /// </summary>
    private static async Task ImportLocalArtworkSetAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        IReadOnlyDictionary<OmniLibraryArtworkSlotKind, string> localArtwork,
        bool crossOrientation,
        CancellationToken cancellationToken)
    {
        foreach (var state in session.Pending())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = crossOrientation
                ? state.Slot.Orientation == OmniLibraryArtworkOrientation.Portrait
                    ? OmniLibraryArtworkSlotKind.Hero
                    : (OmniLibraryArtworkSlotKind?)null
                : state.Slot.LocalKind;
            if (kind is null ||
                !localArtwork.TryGetValue(kind.Value, out var sourcePath) ||
                !OmniLibraryLocalArtworkResolver.TryResolveLocalReference(sourcePath, out var resolvedSourcePath))
            {
                continue;
            }

            await TryCandidateUrlsAsync(
                session,
                state,
                httpClient,
                [new Uri(resolvedSourcePath).AbsoluteUri],
                new ArtworkCandidateOptions(AllowFallback: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Steam's own library files of an installed Steam copy with the same
    /// canonical title (ORBIT's library match). They must pass the strict
    /// orientation check like any other automatic source.
    /// </summary>
    private static async Task ImportSteamLibraryMatchAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        IReadOnlyDictionary<OmniLibraryArtworkSlotKind, IReadOnlyList<string>> matches,
        CancellationToken cancellationToken)
    {
        foreach (var state in session.Pending())
        {
            if (!matches.TryGetValue(state.Slot.LocalKind, out var paths))
            {
                continue;
            }

            await TryCandidateUrlsAsync(
                session,
                state,
                httpClient,
                paths.Select(path => new Uri(path).AbsoluteUri),
                default,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryExtractExecutableIcon(
        string? executablePath,
        out string iconPath)
    {
        iconPath = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
            {
                return false;
            }

            iconPath = Path.Combine(
                Path.GetTempPath(),
                $"steamloader-local-icon-{Guid.NewGuid():N}.png");
            using var bitmap = icon.ToBitmap();
            bitmap.Save(iconPath, ImageFormat.Png);
            return IsUsableArtworkFile(iconPath);
        }
        catch
        {
            DeleteFileIfExists(iconPath);
            iconPath = string.Empty;
            return false;
        }
    }

    private static async Task DownloadRetroAchievementsArtworkSetAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var gameId = target.RetroAchievementsGameId.GetValueOrDefault();
        if (gameId == 0)
        {
            var contentHash = await ManagedRetroAchievementsHasher.HashAsync(
                target.RomSystemId,
                target.RomPath,
                cancellationToken).ConfigureAwait(false);
            using var identifyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "https://retroachievements.org/dorequest.php")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["r"] = "gameid",
                    ["m"] = contentHash,
                }),
            };
            identifyRequest.Headers.TryAddWithoutValidation(
                "User-Agent",
                "ToolsForSteam-OmniLibrary/0.4.1");
            identifyRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var identifyResponse = await httpClient.SendAsync(
                identifyRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            identifyResponse.EnsureSuccessStatusCode();
            await using var identifyStream = await identifyResponse.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var identifyDocument = await JsonDocument.ParseAsync(
                identifyStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!identifyDocument.RootElement.TryGetProperty("GameID", out var gameIdNode) ||
                !TryReadUInt32(gameIdNode, out gameId) ||
                gameId == 0)
            {
                return;
            }
        }

        var gameUrl =
            $"https://retroachievements.org/API/API_GetGame.php?i={gameId}&y={Uri.EscapeDataString(target.RetroAchievementsApiKey)}";
        using var gameResponse = await httpClient.GetAsync(
            gameUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        gameResponse.EnsureSuccessStatusCode();
        await using var gameStream = await gameResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var gameDocument = await JsonDocument.ParseAsync(
            gameStream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = gameDocument.RootElement;
        // Exact content-hash provider metadata. Its images are small, so they
        // usually remain fallbacks that GameTDB, libretro or SteamGridDB upgrade.
        var imageByOrientation = new Dictionary<OmniLibraryArtworkOrientation, string>
        {
            [OmniLibraryArtworkOrientation.Capsule] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageTitle", "imageTitle")),
            [OmniLibraryArtworkOrientation.Portrait] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageBoxArt", "imageBoxArt")),
            [OmniLibraryArtworkOrientation.Hero] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageIngame", "imageIngame")),
            [OmniLibraryArtworkOrientation.Icon] = ResolveRetroAchievementsMediaUrl(
                ReadJsonString(root, "ImageIcon", "imageIcon", "GameIcon", "gameIcon")),
        };

        foreach (var state in session.Pending())
        {
            if (imageByOrientation.TryGetValue(state.Slot.Orientation, out var imageUrl))
            {
                await TryCandidateUrlsAsync(
                    session,
                    state,
                    httpClient,
                    [imageUrl],
                    new ArtworkCandidateOptions(AllowFallback: true),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ReadJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var node) &&
                node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        return string.Empty;
    }

    private static bool TryReadUInt32(JsonElement element, out uint value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetUInt32(out value);
        }
        return element.ValueKind == JsonValueKind.String &&
            uint.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }

    private static string ResolveRetroAchievementsMediaUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            absolute.Scheme == Uri.UriSchemeHttps &&
            IsRetroAchievementsHost(absolute.Host))
        {
            return absolute.AbsoluteUri;
        }
        return value.StartsWith("/", StringComparison.Ordinal)
            ? $"https://media.retroachievements.org{value}"
            : string.Empty;
    }

    private static bool IsRetroAchievementsHost(string host) =>
        host.Equals("retroachievements.org", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".retroachievements.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exact Steam Store search fallback for PC stores (ORBIT
    /// discoverExactStoreArtwork). Only an exact canonical title is accepted.
    /// </summary>
    private static async Task DownloadPublicSteamArtworkSetAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var states = session.Pending().Where(state =>
                state.Slot.Orientation != OmniLibraryArtworkOrientation.Icon)
            .ToArray();
        if (states.Length == 0)
        {
            return;
        }

        var match = await OmniLibraryArtworkDiscovery.FindExactSteamStoreAppAsync(
            httpClient,
            target.Title,
            cancellationToken).ConfigureAwait(false);
        if (match.State != ArtworkAttemptState.Success)
        {
            return;
        }

        foreach (var state in states)
        {
            await TryCandidateUrlsAsync(
                session,
                state,
                httpClient,
                OmniLibraryArtworkDiscovery.SteamStoreArtworkUrls(match.Value, state.Slot.Orientation),
                default,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Official retail scans for Nintendo disc systems outrank community grids,
    /// even when GameTDB's scan is below the 600x900 target.
    /// </summary>
    private static async Task DownloadGameTdbArtworkAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var state = session.Pending(OmniLibraryArtworkOrientation.Portrait).FirstOrDefault();
        if (state is null || !OmniLibraryArtworkDiscovery.IsGameTdbSystem(target.RomSystemId))
        {
            return;
        }

        var covers = await OmniLibraryArtworkDiscovery.FindGameTdbCoversAsync(
            httpClient,
            target.RomSystemId,
            target.Title,
            target.RomPath,
            cancellationToken).ConfigureAwait(false);
        if (covers.State != ArtworkAttemptState.Success)
        {
            return;
        }

        foreach (var cover in covers.Value!)
        {
            if (await TryCandidateUrlsAsync(
                    session,
                    state,
                    httpClient,
                    [cover.Url],
                    new ArtworkCandidateOptions(
                        AllowFallback: true,
                        OfficialScan: true,
                        CropFrontCover: cover.CropFrontCover),
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// libretro-thumbnails for ROMs: portrait only from Named_Boxarts (contained
    /// instead of cropped), wide slots Named_Snaps, then Named_Titles, then
    /// Named_Boxarts. The folder order is semantic: once the best available
    /// role has a valid asset, a lower role never replaces it.
    /// </summary>
    private static async Task DownloadLibretroArtworkAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var playlist = OmniLibraryRomSystemRegistry.All
            .FirstOrDefault(system => system.Id.Equals(
                target.RomSystemId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            ?.LibretroPlaylistName;
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return;
        }

        foreach (var state in session.Pending(
                     OmniLibraryArtworkOrientation.Portrait,
                     OmniLibraryArtworkOrientation.Hero,
                     OmniLibraryArtworkOrientation.Capsule))
        {
            var higherRoleUnavailable = false;
            foreach (var folder in OmniLibraryArtworkRules.LibretroFolders(state.Slot.Orientation))
            {
                var discovered = await OmniLibraryArtworkDiscovery.FindLibretroArtworkAsync(
                    httpClient,
                    playlist,
                    folder,
                    target.Title,
                    target.RomPath,
                    cancellationToken).ConfigureAwait(false);
                if (discovered.State == ArtworkAttemptState.Unavailable)
                {
                    break;
                }
                if (discovered.State == ArtworkAttemptState.Missing)
                {
                    continue;
                }

                var downloaded = await DownloadCandidateAsync(
                    httpClient,
                    discovered.Value!,
                    cancellationToken).ConfigureAwait(false);
                if (downloaded.State == ArtworkAttemptState.Unavailable)
                {
                    higherRoleUnavailable = true;
                    continue;
                }
                if (downloaded.State == ArtworkAttemptState.Missing)
                {
                    continue;
                }

                ArtworkOfferResult result;
                try
                {
                    result = await OfferArtworkAsync(
                        session,
                        state,
                        downloaded.Value!,
                        new ArtworkCandidateOptions(
                            AllowFallback: true,
                            ContainFallback: state.Slot.Orientation == OmniLibraryArtworkOrientation.Portrait &&
                                             folder == LibretroArtworkFolder.Named_Boxarts,
                            ForceFallback: higherRoleUnavailable),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    DeleteFileIfExists(downloaded.Value!.TempPath);
                }

                if (result != ArtworkOfferResult.Rejected)
                {
                    break;
                }
            }
        }
    }

    private static int EnsureCompleteArtworkSetFromExistingAssets(
        string gridDirectory,
        string gridId,
        string title)
    {
        var updated = EnsurePrimaryArtworkSetFromExistingAssets(
            gridDirectory,
            gridId);

        var iconStem = $"{gridId}-icon";
        if (!HasArtworkVariant(gridDirectory, iconStem))
        {
            var iconSource = new[] { $"{gridId}p", gridId, $"{gridId}_hero" }
                .Select(stem => FindArtworkVariant(gridDirectory, stem))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            var iconSlot = ArtworkSlots.First(slot => slot.SlotName == "icon");
            if (!string.IsNullOrWhiteSpace(iconSource) &&
                PromoteStoreFallbackArtwork(
                    gridDirectory,
                    iconStem,
                    new DownloadedArtworkFile(
                        iconSource,
                        Path.GetExtension(iconSource).ToLowerInvariant()),
                    iconSlot))
            {
                updated++;
            }
        }

        var logoStem = $"{gridId}_logo";
        if (!HasArtworkVariant(gridDirectory, logoStem) &&
            GenerateTitleLogo(gridDirectory, logoStem, title))
        {
            updated++;
        }

        return updated;
    }

    private static bool GenerateTitleLogo(
        string gridDirectory,
        string fileStem,
        string title)
    {
        var normalizedTitle = Regex.Replace(
            title?.Trim() ?? string.Empty,
            "\\s+",
            " ");
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-generated-logo-{Guid.NewGuid():N}.png");
        try
        {
            using var image = new Bitmap(1024, 320, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var fontSize = 112f;
            Font? font = null;
            try
            {
                while (fontSize >= 34f)
                {
                    font?.Dispose();
                    font = new Font(
                        "Segoe UI",
                        fontSize,
                        FontStyle.Bold,
                        GraphicsUnit.Pixel);
                    var measured = graphics.MeasureString(normalizedTitle, font);
                    if (measured.Width <= 940 && measured.Height <= 250)
                    {
                        break;
                    }
                    fontSize -= 6f;
                }

                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                };
                var bounds = new RectangleF(22, 20, 980, 280);
                using var shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                using var foreground = new SolidBrush(Color.White);
                graphics.DrawString(
                    normalizedTitle,
                    font!,
                    shadow,
                    new RectangleF(bounds.X + 5, bounds.Y + 6, bounds.Width, bounds.Height),
                    format);
                graphics.DrawString(normalizedTitle, font!, foreground, bounds, format);
                image.Save(temporaryPath, ImageFormat.Png);
            }
            finally
            {
                font?.Dispose();
            }

            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(temporaryPath, ".png"));
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteFileIfExists(temporaryPath);
        }
    }

    private static int EnsurePrimaryArtworkSetFromExistingAssets(
        string gridDirectory,
        string gridId)
    {
        var slots = ArtworkSlots.Where(slot =>
                slot.SlotName is "library capsule" or "portrait" or "hero")
            .ToArray();
        var updated = 0;
        foreach (var slot in slots)
        {
            var targetStem = slot.FileStemBuilder(gridId);
            if (HasArtworkVariant(gridDirectory, targetStem))
            {
                continue;
            }

            var source = slots
                .Where(candidate => !ReferenceEquals(candidate, slot))
                .Select(candidate => FindArtworkVariant(
                    gridDirectory,
                    candidate.FileStemBuilder(gridId)))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            {
                continue;
            }

            if (PromoteStoreFallbackArtwork(
                    gridDirectory,
                    targetStem,
                    new DownloadedArtworkFile(source, extension),
                    slot))
            {
                updated++;
            }
        }

        return updated;
    }

    private static string? FindArtworkVariant(
        string gridDirectory,
        string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (IsUsableArtworkFile(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Provider catalog URLs (ORBIT remoteProviderMetadataCandidates). The
    /// cross-orientation pass follows providerMetadataFallbackCandidates.
    /// </summary>
    private static async Task DownloadRemoteStoreArtworkSetAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        StoreSyncArtworkTarget target,
        bool crossOrientation,
        CancellationToken cancellationToken)
    {
        var portraitUrl = target.FallbackPortraitUrl?.Trim() ?? string.Empty;
        var heroUrl = target.FallbackHeroUrl?.Trim() ?? string.Empty;
        // Each slot first takes the provider image made for its own shape.
        var candidates = new Dictionary<OmniLibraryArtworkOrientation, string[]>
        {
            [OmniLibraryArtworkOrientation.Capsule] = [heroUrl],
            [OmniLibraryArtworkOrientation.Portrait] = [portraitUrl],
            [OmniLibraryArtworkOrientation.Hero] = [heroUrl],
        };
        // A portrait may fall back to the provider's wide art. A large wide
        // slot must not silently adopt a portrait cover; the exact Steam Store
        // search and SteamGridDB are the safer fallbacks there.
        var fallbackCandidates = new Dictionary<OmniLibraryArtworkOrientation, string[]>
        {
            [OmniLibraryArtworkOrientation.Portrait] = [heroUrl],
        };

        foreach (var state in session.Pending())
        {
            if (!(crossOrientation ? fallbackCandidates : candidates).TryGetValue(
                    state.Slot.Orientation,
                    out var urls))
            {
                continue;
            }

            await TryCandidateUrlsAsync(
                session,
                state,
                httpClient,
                // file:// and rooted references were consumed by the local stage.
                urls.Where(url => !OmniLibraryLocalArtworkResolver.TryResolveLocalReference(url, out _)),
                new ArtworkCandidateOptions(AllowFallback: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<StoreSyncArtworkMatch?> ResolveMatchAsync(
        string title,
        IReadOnlyList<string> searchHints,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // ORBIT matches the game's title only; path-derived hints are guesses.
        _ = searchHints;
        return await FindGameMatchAsync(httpClient, title, cancellationToken);
    }

    public async Task<StoreSyncArtworkPreview?> ResolvePreviewAsync(
        string title,
        IReadOnlyList<string> searchHints,
        string apiKey,
        int? cachedGameId,
        string? cachedMatchName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://www.steamgriddb.com/api/v2/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _ = searchHints;
        var match = cachedGameId.HasValue && cachedGameId.Value > 0
            ? new StoreSyncArtworkMatch(cachedGameId.Value, cachedMatchName ?? title)
            : await FindGameMatchAsync(httpClient, title, cancellationToken);
        if (match is null)
        {
            return null;
        }

        var assetUrl = await FindPreviewAssetUrlAsync(httpClient, match.GameId, cancellationToken);
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return null;
        }

        var previewAsset = await DownloadAssetContentAsync(httpClient, assetUrl, cancellationToken);
        if (previewAsset is null)
        {
            return null;
        }

        return new StoreSyncArtworkPreview(
            previewAsset.DataUri,
            match.GameId,
            string.IsNullOrWhiteSpace(match.MatchName) ? title : match.MatchName);
    }

    public string GetEffectiveApiKey(string? configuredApiKey)
    {
        return string.IsNullOrWhiteSpace(configuredApiKey)
            ? BuiltInApiKey
            : configuredApiKey.Trim();
    }

    public string GetPreview(string? configuredApiKey)
    {
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return "Built in";
        }

        var trimmedApiKey = configuredApiKey.Trim();
        return trimmedApiKey.Length <= 6
            ? "Configured"
            : $"Configured ({trimmedApiKey[^4..]})";
    }

    private static async Task<StoreSyncArtworkMatch?> FindGameMatchAsync(
        HttpClient httpClient,
        string title,
        CancellationToken cancellationToken)
    {
        var found = await OmniLibraryArtworkDiscovery.FindSteamGridDbGameAsync(
            httpClient,
            title,
            cancellationToken).ConfigureAwait(false);
        return found.State == ArtworkAttemptState.Success
            ? new StoreSyncArtworkMatch(found.Value!.GameId, found.Value.Name)
            : null;
    }

    /// <summary>
    /// SteamGridDB as the final source (ORBIT trySteamGridDb): an exact title
    /// match, then at most four ranked assets per slot, each validated. Icons
    /// are left to the provider and the game's own executable.
    /// </summary>
    private static async Task DownloadArtworkSetAsync(
        ArtworkSlotSession session,
        HttpClient httpClient,
        HttpClient steamGridDbClient,
        StoreSyncArtworkTarget target,
        CancellationToken cancellationToken)
    {
        var states = session.Pending().Where(state =>
                state.Slot.Orientation != OmniLibraryArtworkOrientation.Icon)
            .ToArray();
        if (states.Length == 0)
        {
            return;
        }

        // A cached match is reused only when it names this exact title. Older
        // fuzzy matches are searched again under the exact rules.
        var titleKeys = new[]
            {
                OmniLibraryArtworkRules.SteamGridDbSearchKey(target.Title),
                OmniLibraryArtworkRules.SteamGridDbSearchKey(
                    OmniLibraryArtworkRules.StripSteamGridDbEditionWords(target.Title)),
            }
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        int gameId;
        if (target.CachedGameId is > 0 &&
            titleKeys.Contains(OmniLibraryArtworkRules.SteamGridDbSearchKey(target.CachedMatchName)))
        {
            gameId = target.CachedGameId.Value;
        }
        else
        {
            var match = await FindGameMatchAsync(steamGridDbClient, target.Title, cancellationToken)
                .ConfigureAwait(false);
            if (match is null)
            {
                return;
            }
            gameId = match.GameId;
        }

        foreach (var state in states)
        {
            var assets = await OmniLibraryArtworkDiscovery.FindSteamGridDbAssetsAsync(
                steamGridDbClient,
                gameId,
                state.Slot.Orientation,
                cancellationToken).ConfigureAwait(false);
            if (assets.State != ArtworkAttemptState.Success)
            {
                continue;
            }

            // A community asset can disappear independently of the search API.
            // Try a small ranked set instead of letting one stale first asset
            // suppress every valid alternative. Downloads use the plain client
            // so the API key never travels to the CDN.
            await TryCandidateUrlsAsync(
                session,
                state,
                httpClient,
                assets.Value!.Take(4),
                default,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryApplyStoreBadgeAsync(
        string gridDirectory,
        string gridId,
        ArtworkSlot slot,
        string? storeId,
        CancellationToken cancellationToken)
    {
        if (!slot.SupportsBadge || string.IsNullOrWhiteSpace(storeId))
        {
            return;
        }

        var fileStem = slot.FileStemBuilder(gridId);
        var imagePath = FindArtworkFile(gridDirectory, fileStem);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            await StoreBadgeCompositor.ApplyBadgeAsync(imagePath, storeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindArtworkFile(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static async Task<string?> FindPreviewAssetUrlAsync(
        HttpClient httpClient,
        int gameId,
        CancellationToken cancellationToken)
    {
        foreach (var requestPath in new[]
                 {
                     $"grids/game/{gameId}?types=static&dimensions=460x215,920x430&mimes=image/png,image/jpeg,image/webp",
                     $"grids/game/{gameId}?types=static&mimes=image/png,image/jpeg,image/webp",
                 })
        {
            var response = await httpClient.GetAsync(requestPath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamGridDbListResponse<SteamGridDbAsset>>(
                responseStream,
                JsonOptions,
                cancellationToken);

            var assetUrl = payload?.Data?
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Url))
                .OrderBy(asset => ScorePreviewAsset(asset))
                .Select(asset => asset.Url)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(assetUrl))
            {
                return assetUrl;
            }
        }

        return null;
    }

    private static async Task<DownloadedArtworkFile?> DownloadAssetToTemporaryFileAsync(
        HttpClient httpClient,
        string assetUrl,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(assetUrl, UriKind.Absolute, out var localUri) &&
            localUri.IsFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = localUri.LocalPath;
            if (!File.Exists(localPath))
            {
                return null;
            }

            var localExtension = ResolveFileExtension(localPath, null);
            var localLength = new FileInfo(localPath).Length;
            if (localExtension is null ||
                localLength < MinimumArtworkBytes ||
                localLength > MaximumArtworkBytes)
            {
                return null;
            }

            var localTempPath = Path.Combine(
                Path.GetTempPath(),
                $"steamloader-grid-{Guid.NewGuid():N}{localExtension}");
            try
            {
                await using var localInput = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                await using (var localOutput = File.Create(localTempPath))
                {
                    await CopyArtworkWithLimitAsync(
                        localInput,
                        localOutput,
                        cancellationToken).ConfigureAwait(false);
                    await localOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!IsUsableArtworkFile(localTempPath))
                {
                    DeleteFileIfExists(localTempPath);
                    return null;
                }
                return new DownloadedArtworkFile(localTempPath, localExtension);
            }
            catch
            {
                DeleteFileIfExists(localTempPath);
                throw;
            }
        }

        using var response = await httpClient.GetAsync(
            assetUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            return null;
        }

        var extension = ResolveFileExtension(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (extension is null)
        {
            return null;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"steamloader-grid-{Guid.NewGuid():N}{extension}");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = File.Create(tempPath))
            {
                await CopyArtworkWithLimitAsync(input, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!IsUsableArtworkFile(tempPath))
            {
                DeleteFileIfExists(tempPath);
                return null;
            }
            return new DownloadedArtworkFile(tempPath, extension);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    private static async Task<DownloadedArtworkContent?> DownloadAssetContentAsync(
        HttpClient httpClient,
        string assetUrl,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            assetUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            return null;
        }

        var mimeType = ResolveMimeType(assetUrl, response.Content.Headers.ContentType?.MediaType);
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        await CopyArtworkWithLimitAsync(input, output, cancellationToken).ConfigureAwait(false);
        return new DownloadedArtworkContent(
            $"data:{mimeType};base64,{Convert.ToBase64String(output.ToArray())}");
    }

    private static bool PromoteDownloadedArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset)
    {
        if (!IsUsableArtworkFile(asset.TempPath))
        {
            return false;
        }

        var targetPath = Path.Combine(gridDirectory, fileStem + asset.Extension);
        Directory.CreateDirectory(gridDirectory);

        if (File.Exists(targetPath) && FilesAreEqual(asset.TempPath, targetPath))
        {
            RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
            return false;
        }

        var stagingPath = Path.Combine(
            gridDirectory,
            $".tfs-artwork-{fileStem}-{Guid.NewGuid():N}{asset.Extension}.tmp");
        try
        {
            File.Copy(asset.TempPath, stagingPath, overwrite: false);
            File.Move(stagingPath, targetPath, overwrite: true);
            ArtworkValidationCache.TryRemove(targetPath, out _);
            RemoveSlotVariantsExcept(gridDirectory, fileStem, targetPath);
            return true;
        }
        finally
        {
            DeleteFileIfExists(stagingPath);
        }
    }

    private static bool PromoteStoreFallbackArtwork(
        string gridDirectory,
        string fileStem,
        DownloadedArtworkFile asset,
        ArtworkSlot slot)
    {
        if (!slot.PreferredWidth.HasValue || !slot.PreferredHeight.HasValue)
        {
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }

        var outputExtension = asset.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              asset.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : ".png";
        var resizedPath = Path.Combine(
            Path.GetTempPath(),
            $"steamloader-grid-fallback-{Guid.NewGuid():N}{outputExtension}");
        try
        {
            using var inputStream = new FileStream(
                asset.TempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var source = Image.FromStream(inputStream);
            var targetWidth = slot.PreferredWidth.Value;
            var targetHeight = slot.PreferredHeight.Value;
            using var output = new Bitmap(
                targetWidth,
                targetHeight,
                PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.Clear(Color.Black);
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                var scale = Math.Max(
                    (double)targetWidth / Math.Max(1, source.Width),
                    (double)targetHeight / Math.Max(1, source.Height));
                var scaledWidth = Math.Max(1, (int)Math.Ceiling(source.Width * scale));
                var scaledHeight = Math.Max(1, (int)Math.Ceiling(source.Height * scale));
                var x = (targetWidth - scaledWidth) / 2;
                var y = (targetHeight - scaledHeight) / 2;
                graphics.DrawImage(source, x, y, scaledWidth, scaledHeight);
            }

            output.Save(
                resizedPath,
                outputExtension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? ImageFormat.Jpeg
                    : ImageFormat.Png);
            return PromoteDownloadedArtwork(
                gridDirectory,
                fileStem,
                new DownloadedArtworkFile(resizedPath, outputExtension));
        }
        catch
        {
            // A catalog image is still preferable to an empty Steam slot if the
            // platform image decoder cannot resize a particular source format.
            return PromoteDownloadedArtwork(gridDirectory, fileStem, asset);
        }
        finally
        {
            DeleteFileIfExists(resizedPath);
        }
    }

    private static string? ResolveFileExtension(string assetUrl, string? contentType)
    {
        var contentTypeExtension = contentType?.Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/vnd.microsoft.icon" => ".ico",
            "image/x-icon" => ".ico",
            "image/webp" => ".webp",
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(contentTypeExtension))
        {
            return contentTypeExtension;
        }

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
        {
            return null;
        }

        var extension = Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".ico" or ".webp"
            ? extension
            : null;
    }

    private static string? ResolveMimeType(string assetUrl, string? contentType)
    {
        var normalizedContentType = contentType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedContentType) &&
            normalizedContentType.StartsWith("image/", StringComparison.Ordinal))
        {
            return normalizedContentType;
        }

        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out var assetUri))
        {
            return null;
        }

        return Path.GetExtension(assetUri.AbsolutePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            _ => null,
        };
    }

    private static double ScorePreviewAsset(SteamGridDbAsset asset)
    {
        if (!asset.Width.HasValue || !asset.Height.HasValue || asset.Height.Value <= 0)
        {
            return 0;
        }

        const double preferredRatio = 460d / 215d;
        var actualRatio = (double)asset.Width.Value / asset.Height.Value;
        var ratioScore = Math.Abs(preferredRatio - actualRatio) * 1000d;
        var preferredArea = 460d * 215d;
        var actualArea = Math.Max(1d, asset.Width.Value * asset.Height.Value);
        var areaScore = Math.Abs(Math.Log(actualArea / preferredArea));
        return ratioScore + areaScore;
    }

    private static void RemoveSlotVariantsExcept(string gridDirectory, string fileStem, string keepPath)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".ico", ".webp" })
        {
            var path = Path.Combine(gridDirectory, fileStem + extension);
            if (string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    ArtworkValidationCache.TryRemove(path, out _);
                }
                catch
                {
                    // Steam may briefly hold an older extension open. The new
                    // atomic target is already valid; a later pass can remove it.
                }
            }
        }
    }

    private static int PromoteReloadedArtworkSet(
        string stagingDirectory,
        string gridDirectory,
        string gridId)
    {
        var updated = 0;
        foreach (var fileStem in GetArtworkFileStems(gridId))
        {
            var sourcePath = FindArtworkFile(stagingDirectory, fileStem);
            if (sourcePath is null)
            {
                return 0;
            }

            var extension = Path.GetExtension(sourcePath);
            if (PromoteDownloadedArtwork(
                    gridDirectory,
                    fileStem,
                    new DownloadedArtworkFile(sourcePath, extension)))
            {
                updated++;
            }
        }

        return updated;
    }

    private static IReadOnlyList<string> GetArtworkFileStems(string gridId) =>
    [
        gridId,
        $"{gridId}p",
        $"{gridId}_hero",
        $"{gridId}_logo",
        $"{gridId}-icon",
    ];

    private static void DeleteArtworkReloadStagingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Name.StartsWith(
                    ".tfs-artwork-reload-",
                    StringComparison.OrdinalIgnoreCase))
            {
                directory.Delete(recursive: true);
            }
        }
        catch
        {
            // A later stale-staging cleanup can remove a briefly locked file.
        }
    }

    private static bool FilesAreEqual(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(leftPath);
        using var rightStream = File.OpenRead(rightPath);
        using var leftHash = SHA256.Create();
        using var rightHash = SHA256.Create();
        return leftHash.ComputeHash(leftStream).SequenceEqual(rightHash.ComputeHash(rightStream));
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal static bool HasPrimaryArtworkSet(string gridDirectory, uint appId)
    {
        return GetMissingPrimaryArtworkSlots(gridDirectory, appId).Count == 0;
    }

    internal static bool HasCompleteArtworkSet(string gridDirectory, uint appId)
    {
        return GetMissingArtworkSlots(gridDirectory, appId).Count == 0;
    }

    internal static IReadOnlyList<string> GetMissingArtworkSlots(
        string gridDirectory,
        uint appId)
    {
        var gridId = SteamShortcutIds.BuildGridId(appId);
        var missing = GetMissingPrimaryArtworkSlots(gridDirectory, appId).ToList();
        if (!HasArtworkVariant(gridDirectory, $"{gridId}_logo"))
        {
            missing.Add("logo");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}-icon"))
        {
            missing.Add("icon");
        }
        return missing;
    }

    internal static IReadOnlyList<string> GetMissingPrimaryArtworkSlots(
        string gridDirectory,
        uint appId)
    {
        var gridId = SteamShortcutIds.BuildGridId(appId);
        var missing = new List<string>(3);
        if (!HasArtworkVariant(gridDirectory, gridId))
        {
            missing.Add("library capsule");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}p"))
        {
            missing.Add("portrait");
        }
        if (!HasArtworkVariant(gridDirectory, $"{gridId}_hero"))
        {
            missing.Add("hero");
        }

        return missing;
    }

    private static bool HasArtworkVariant(string gridDirectory, string fileStem)
    {
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" })
        {
            if (IsUsableArtworkFile(Path.Combine(gridDirectory, fileStem + extension)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task CopyArtworkWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > MaximumArtworkBytes)
            {
                throw new InvalidOperationException(
                    "The artwork download exceeded its 32 MB safety limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsUsableArtworkFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists ||
                file.Length < MinimumArtworkBytes)
            {
                return false;
            }

            if (ArtworkValidationCache.TryGetValue(path, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks)
            {
                return cached.Usable;
            }

            Span<byte> header = stackalloc byte[12];
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            var png = header[0] == 0x89 && header[1] == 0x50 &&
                header[2] == 0x4e && header[3] == 0x47 &&
                header[4] == 0x0d && header[5] == 0x0a &&
                header[6] == 0x1a && header[7] == 0x0a;
            var jpeg = header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
            var webp = header[0] == (byte)'R' && header[1] == (byte)'I' &&
                header[2] == (byte)'F' && header[3] == (byte)'F' &&
                header[8] == (byte)'W' && header[9] == (byte)'E' &&
                header[10] == (byte)'B' && header[11] == (byte)'P';
            var icon = header[0] == 0 && header[1] == 0 &&
                header[2] == 1 && header[3] == 0;
            var usable = png || jpeg || webp || icon;
            if (ArtworkValidationCache.Count >= MaximumArtworkValidationEntries)
            {
                ArtworkValidationCache.Clear();
            }
            ArtworkValidationCache[path] = new ArtworkValidationEntry(
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                usable);
            return usable;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupStaleArtworkStagingFiles(string gridDirectory)
    {
        var now = DateTime.UtcNow;
        var previousTicks = Interlocked.Read(ref _lastStagingCleanupUtcTicks);
        if (previousTicks > 0 &&
            now - new DateTime(previousTicks, DateTimeKind.Utc) < TimeSpan.FromHours(1))
        {
            return;
        }
        if (Interlocked.CompareExchange(
                ref _lastStagingCleanupUtcTicks,
                now.Ticks,
                previousTicks) != previousTicks)
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         gridDirectory,
                         ".tfs-artwork-*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < now.Subtract(TimeSpan.FromHours(1)))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         gridDirectory,
                         ".tfs-artwork-reload-*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) <
                        now.Subtract(TimeSpan.FromHours(1)))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private sealed record ArtworkSlot(
        string SlotName,
        Func<string, string> FileStemBuilder,
        int? PreferredWidth,
        int? PreferredHeight,
        OmniLibraryArtworkOrientation Orientation,
        OmniLibraryArtworkSlotKind LocalKind,
        bool SupportsBadge = false);

    private readonly record struct ArtworkValidationEntry(
        long Length,
        long LastWriteTicks,
        bool Usable);

    private sealed record SteamGridDbListResponse<T>(
        bool Success,
        IReadOnlyList<T> Data);

    private sealed record SteamGridDbAsset(
        string Url,
        int? Width,
        int? Height);

    private sealed record DownloadedArtworkFile(
        string TempPath,
        string Extension);

    private sealed record DownloadedArtworkContent(
        string DataUri);
}

internal sealed record StoreSyncArtworkTarget(
    string TitleId,
    string Title,
    uint AppId,
    IReadOnlyList<string> SearchHints,
    int? CachedGameId,
    string CachedMatchName,
    string StoreId = "",
    string FallbackPortraitUrl = "",
    string FallbackHeroUrl = "",
    string RomPath = "",
    string RomSystemId = "",
    string RetroAchievementsApiKey = "",
    uint? RetroAchievementsGameId = null,
    string LocalInstallPath = "",
    string LocalExecutablePath = "",
    bool ForceReload = false);

internal sealed record StoreSyncArtworkSummary(
    int UpdatedTitleCount,
    int UpdatedFileCount,
    IReadOnlyList<string> UpdatedTitleIds,
    int FailureCount = 0,
    IReadOnlyList<string>? FailureDetails = null);

internal sealed record StoreSyncArtworkMatch(
    int GameId,
    string MatchName);

internal sealed record StoreSyncArtworkPreview(
    string DataUri,
    int GameId,
    string MatchName);
