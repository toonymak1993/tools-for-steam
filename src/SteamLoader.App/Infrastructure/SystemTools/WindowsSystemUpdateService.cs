using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamLoader.App.Infrastructure.SystemTools;

public sealed class WindowsSystemUpdateService
{
    private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private WindowsSystemUpdateSnapshot _snapshot = WindowsSystemUpdateSnapshot.Empty;

    public WindowsSystemUpdateSnapshot GetSnapshot()
    {
        lock (_snapshotGate)
        {
            return _snapshot with { Scanning = _scanGate.CurrentCount == 0 };
        }
    }

    public async Task<WindowsSystemUpdateSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken))
        {
            return GetSnapshot();
        }

        try
        {
            SetScanningSnapshot();
            var updates = await Task.Run(SearchForUpdates, cancellationToken);
            var snapshot = new WindowsSystemUpdateSnapshot(
                Scanning: false,
                LastCheckedAt: DateTimeOffset.Now,
                Updates: updates,
                StatusText: updates.Count == 0
                    ? "Windows is up to date."
                    : updates.Count == 1
                        ? "1 Windows update is available."
                        : $"{updates.Count} Windows updates are available.",
                ActionStartedAt: GetSnapshot().ActionStartedAt);
            SetSnapshot(snapshot);
            return snapshot;
        }
        catch
        {
            lock (_snapshotGate)
            {
                _snapshot = _snapshot with
                {
                    Scanning = false,
                    StatusText = "Windows Update could not be checked."
                };
            }

            throw;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public WindowsSystemUpdateSnapshot RunWindowsUpdate()
    {
        using var process = Process.Start(CreateWindowsUpdateStartInfo())
            ?? throw new InvalidOperationException("Windows Update could not be opened.");

        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                ActionStartedAt = DateTimeOffset.Now,
                StatusText = "Windows Update is handling the download and installation."
            };
            return _snapshot;
        }
    }

    internal static ProcessStartInfo CreateWindowsUpdateStartInfo()
        => new("ms-settings:windowsupdate-action")
        {
            UseShellExecute = true
        };

    private void SetScanningSnapshot()
    {
        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                Scanning = true,
                StatusText = "Checking Windows Update..."
            };
        }
    }

    private void SetSnapshot(WindowsSystemUpdateSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }
    }

    private static IReadOnlyList<WindowsSystemUpdateItem> SearchForUpdates()
    {
        object? sessionObject = null;
        object? searcherObject = null;
        object? resultObject = null;
        object? updatesObject = null;

        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                ?? throw new InvalidOperationException("Windows Update Agent is not available.");
            sessionObject = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException("Windows Update Agent could not be started.");
            dynamic session = sessionObject;
            session.ClientApplicationID = "Tools for Steam";

            searcherObject = session.CreateUpdateSearcher();
            dynamic searcher = searcherObject;
            resultObject = searcher.Search(SearchCriteria);
            dynamic result = resultObject;
            updatesObject = result.Updates;
            dynamic updates = updatesObject;

            var items = new List<WindowsSystemUpdateItem>(Convert.ToInt32(updates.Count));
            for (var index = 0; index < Convert.ToInt32(updates.Count); index++)
            {
                object? updateObject = null;
                object? identityObject = null;
                object? kbObject = null;
                object? behaviorObject = null;

                try
                {
                    updateObject = updates.Item(index);
                    dynamic update = updateObject;
                    identityObject = update.Identity;
                    dynamic identity = identityObject;
                    kbObject = update.KBArticleIDs;
                    behaviorObject = update.InstallationBehavior;

                    items.Add(new WindowsSystemUpdateItem(
                        Id: Convert.ToString(identity.UpdateID) ?? string.Empty,
                        Title: Convert.ToString(update.Title) ?? "Windows update",
                        Kind: Convert.ToInt32(update.Type) == 2 ? "Driver" : "Software",
                        KbArticleIds: ReadStringCollection(kbObject),
                        DownloadSizeBytes: Math.Max(0L, Convert.ToInt64(update.MaxDownloadSize)),
                        IsDownloaded: Convert.ToBoolean(update.IsDownloaded),
                        IsMandatory: Convert.ToBoolean(update.IsMandatory),
                        MayRequireRestart: ReadRebootBehavior(behaviorObject) != 0));
                }
                finally
                {
                    ReleaseComObject(behaviorObject);
                    ReleaseComObject(kbObject);
                    ReleaseComObject(identityObject);
                    ReleaseComObject(updateObject);
                }
            }

            return items
                .OrderByDescending(item => item.IsMandatory)
                .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            ReleaseComObject(updatesObject);
            ReleaseComObject(resultObject);
            ReleaseComObject(searcherObject);
            ReleaseComObject(sessionObject);
        }
    }

    private static IReadOnlyList<string> ReadStringCollection(object? collectionObject)
    {
        if (collectionObject is null)
        {
            return [];
        }

        dynamic collection = collectionObject;
        var values = new List<string>();
        for (var index = 0; index < Convert.ToInt32(collection.Count); index++)
        {
            var value = Convert.ToString(collection.Item(index));
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        return values;
    }

    private static int ReadRebootBehavior(object? behaviorObject)
    {
        if (behaviorObject is null)
        {
            return 0;
        }

        dynamic behavior = behaviorObject;
        return Convert.ToInt32(behavior.RebootBehavior);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }
}

public sealed record WindowsSystemUpdateSnapshot(
    bool Scanning,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<WindowsSystemUpdateItem> Updates,
    string StatusText,
    DateTimeOffset? ActionStartedAt)
{
    public static WindowsSystemUpdateSnapshot Empty { get; } = new(
        Scanning: false,
        LastCheckedAt: null,
        Updates: [],
        StatusText: "Open Windows Update to check for available updates.",
        ActionStartedAt: null);
}

public sealed record WindowsSystemUpdateItem(
    string Id,
    string Title,
    string Kind,
    IReadOnlyList<string> KbArticleIds,
    long DownloadSizeBytes,
    bool IsDownloaded,
    bool IsMandatory,
    bool MayRequireRestart);
