using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Which emulator a system uses right now, and which one TFS would install
/// when none is ready. <see cref="InstallCandidate"/> is the explicit choice or
/// the recommended installable emulator.
/// </summary>
internal sealed record OmniLibraryEmulatorSelection(
    OmniLibraryEmulatorDescriptor? Emulator,
    string ExecutablePath,
    string CorePath,
    OmniLibraryEmulatorDescriptor? InstallCandidate)
{
    public bool Ready => Emulator is not null && ExecutablePath.Length > 0;
}

/// <summary>
/// Finds installed emulators the way ORBIT does: TFS' own managed folder,
/// ORBIT's managed folder (so emulators ORBIT already installed are reused),
/// standard install folders, PATH, Windows App Paths and WinGet. Results are
/// cached briefly because Quick Access snapshots are built often.
/// </summary>
internal static class OmniLibraryEmulatorDetector
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime At, string Path)> LocateCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static (DateTime At, string ExecutablePath, IReadOnlyList<string> Cores)? CoreCache;

    public static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Tools for Steam",
        "Emulators");

    public static string ManagedDirectory(string emulatorId) =>
        Path.Combine(ManagedRoot, emulatorId);

    public static string OrbitManagedDirectory(string emulatorId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ORBIT",
        "Emulators",
        emulatorId);

    public static void Invalidate()
    {
        lock (Gate)
        {
            LocateCache.Clear();
            CoreCache = null;
        }
    }

    public static OmniLibraryEmulatorSelection Resolve(
        OmniLibraryRomSystemDescriptor system,
        string? configuredEmulatorId,
        string? configuredExecutablePath)
    {
        var emulators = OmniLibraryEmulatorCatalog.ForSystem(system);
        var configuredPath = NormalizeExistingFile(configuredExecutablePath);
        if (configuredPath.Length > 0 &&
            OmniLibraryEmulatorCatalog.MatchExecutable(system, configuredPath) is { } configured)
        {
            var core = CoreFor(configured, configuredPath, system);
            if (configured.Id != OmniLibraryEmulatorCatalog.RetroArchId || core.Length > 0)
            {
                return new(configured, configuredPath, core, configured);
            }

            return new(null, string.Empty, string.Empty, configured);
        }

        var preferred = emulators.FirstOrDefault(emulator =>
            emulator.Id.Equals(configuredEmulatorId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            var path = Locate(preferred);
            var core = CoreFor(preferred, path, system);
            return path.Length > 0 && (preferred.Id != OmniLibraryEmulatorCatalog.RetroArchId || core.Length > 0)
                ? new(preferred, path, core, preferred)
                : new(null, string.Empty, string.Empty, preferred);
        }

        foreach (var emulator in emulators)
        {
            var path = Locate(emulator);
            if (path.Length == 0)
            {
                continue;
            }

            var core = CoreFor(emulator, path, system);
            if (emulator.Id == OmniLibraryEmulatorCatalog.RetroArchId && core.Length == 0)
            {
                continue;
            }

            return new(emulator, path, core, emulator);
        }

        return new(null, string.Empty, string.Empty, emulators.FirstOrDefault(emulator => emulator.CanInstall));
    }

    public static string Locate(OmniLibraryEmulatorDescriptor emulator)
    {
        lock (Gate)
        {
            if (LocateCache.TryGetValue(emulator.Id, out var cached) &&
                DateTime.UtcNow - cached.At < CacheLifetime &&
                (cached.Path.Length == 0 || File.Exists(cached.Path)))
            {
                return cached.Path;
            }
        }

        string found;
        try
        {
            found = LocateUncached(emulator);
        }
        catch
        {
            found = string.Empty;
        }

        lock (Gate)
        {
            LocateCache[emulator.Id] = (DateTime.UtcNow, found);
        }

        return found;
    }

    /// <summary>Installed RetroArch cores (full paths) for the given RetroArch executable.</summary>
    public static IReadOnlyList<string> RetroArchCoreFiles(string retroArchExecutable)
    {
        lock (Gate)
        {
            if (CoreCache is { } cached &&
                DateTime.UtcNow - cached.At < CacheLifetime &&
                cached.ExecutablePath.Equals(retroArchExecutable, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Cores;
            }
        }

        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(retroArchExecutable))
        {
            directories.Add(Path.Combine(Path.GetDirectoryName(retroArchExecutable) ?? string.Empty, "cores"));
        }

        directories.Add(Path.Combine(ManagedDirectory(OmniLibraryEmulatorCatalog.RetroArchId), "cores"));
        directories.Add(Path.Combine(OrbitManagedDirectory(OmniLibraryEmulatorCatalog.RetroArchId), "cores"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RetroArch",
            "cores"));
        directories.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RetroArch",
            "cores"));

        var cores = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var core in Directory.EnumerateFiles(directory, "*_libretro.dll"))
                {
                    if (seen.Add(Path.GetFileName(core)))
                    {
                        cores.Add(core);
                    }
                }
            }
            catch
            {
                // RetroArch stays visible even when no cores are installed yet.
            }
        }

        lock (Gate)
        {
            CoreCache = (DateTime.UtcNow, retroArchExecutable, cores);
        }

        return cores;
    }

    /// <summary>The preferred installed core for a system, or empty.</summary>
    public static string MatchingRetroArchCore(
        OmniLibraryRomSystemDescriptor system,
        IReadOnlyList<string> coreFiles)
    {
        foreach (var preferred in system.RetroArchCores)
        {
            var match = coreFiles.FirstOrDefault(path => Path.GetFileName(path)
                .Contains($"{preferred}_libretro", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return string.Empty;
    }

    private static string CoreFor(
        OmniLibraryEmulatorDescriptor emulator,
        string executablePath,
        OmniLibraryRomSystemDescriptor system) =>
        emulator.Id == OmniLibraryEmulatorCatalog.RetroArchId && executablePath.Length > 0
            ? MatchingRetroArchCore(system, RetroArchCoreFiles(executablePath))
            : string.Empty;

    private static string LocateUncached(OmniLibraryEmulatorDescriptor emulator)
    {
        var managed = NestedExecutable(
            [ManagedDirectory(emulator.Id), OrbitManagedDirectory(emulator.Id)],
            emulator.ExecutableNames);
        if (managed.Length > 0)
        {
            return managed;
        }

        var candidates = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var folder in emulator.InstallFolderNames)
            {
                candidates.AddRange(emulator.ExecutableNames.Select(name => Path.Combine(root, folder, name)));
            }
        }

        if (emulator.Id == OmniLibraryEmulatorCatalog.RetroArchId &&
            SteamInstallPath() is { Length: > 0 } steam)
        {
            candidates.Add(Path.Combine(steam, "steamapps", "common", "RetroArch", "retroarch.exe"));
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.AddRange(emulator.ExecutableNames.Select(name => Path.Combine(directory.Trim('"'), name)));
        }

        foreach (var candidate in candidates)
        {
            var existing = NormalizeExistingFile(candidate);
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var registered = RegisteredExecutable(emulator.ExecutableNames);
        if (registered.Length > 0)
        {
            return registered;
        }

        return WingetExecutable(emulator);
    }

    private static string RegisteredExecutable(IReadOnlyList<string> executableNames)
    {
        foreach (var name in executableNames)
        {
            foreach (var (hive, path) in new[]
                     {
                         (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
                         (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
                         (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
                     })
            {
                try
                {
                    using var key = hive.OpenSubKey($@"{path}\{name}");
                    if (key?.GetValue(null) is string value &&
                        NormalizeExistingFile(value.Trim('"')) is { Length: > 0 } existing &&
                        Path.GetFileName(existing).Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }
                }
                catch
                {
                }
            }
        }

        return string.Empty;
    }

    private static string WingetExecutable(OmniLibraryEmulatorDescriptor emulator)
    {
        if (string.IsNullOrWhiteSpace(emulator.WingetPackageId))
        {
            return string.Empty;
        }

        var winget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet");
        foreach (var name in emulator.ExecutableNames)
        {
            var linked = NormalizeExistingFile(Path.Combine(winget, "Links", name));
            if (linked.Length > 0)
            {
                return linked;
            }
        }

        try
        {
            var packages = Path.Combine(winget, "Packages");
            if (!Directory.Exists(packages))
            {
                return string.Empty;
            }

            var roots = Directory.EnumerateDirectories(packages, $"{emulator.WingetPackageId}_*").ToArray();
            return NestedExecutable(roots, emulator.ExecutableNames);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Breadth-first search, so the shallowest copy wins.</summary>
    internal static string NestedExecutable(
        IEnumerable<string> roots,
        IReadOnlyList<string> executableNames,
        int maximumDepth = 6)
    {
        var wanted = new HashSet<string>(executableNames, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>(
            roots.Where(Directory.Exists).Select(root => (root, 0)));
        var visited = 0;
        while (queue.Count > 0 && visited < 20_000)
        {
            var (current, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    visited++;
                    if (wanted.Contains(Path.GetFileName(file)))
                    {
                        return Path.GetFullPath(file);
                    }
                }

                if (depth >= maximumDepth)
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    visited++;
                    var name = Path.GetFileName(directory);
                    if (name.StartsWith('.') ||
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    queue.Enqueue((directory, depth + 1));
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string NormalizeExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            return File.Exists(fullPath) &&
                   Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (key?.GetValue("SteamPath") as string)?.Replace('/', '\\') ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
