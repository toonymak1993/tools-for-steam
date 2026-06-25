using Microsoft.Win32;
using SteamLoader.App.Models;

namespace SteamLoader.App.Infrastructure.Steam;

public sealed class SteamInstallationService
{
    private readonly object _gate = new();
    private readonly SteamInstallPathSettingsStore _settingsStore;
    private readonly string _fallbackSteamRootPath;

    public SteamInstallationService(
        SteamInstallPathSettingsStore settingsStore,
        string fallbackSteamRootPath)
    {
        _settingsStore = settingsStore;
        _fallbackSteamRootPath = fallbackSteamRootPath;
    }

    public SteamPathState GetState()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            return BuildState(configuration);
        }
    }

    public SteamPathState SetManualOverridePath(string value)
    {
        lock (_gate)
        {
            var normalizedPath = NormalizeSteamRootCandidate(value);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new InvalidOperationException("A Steam folder path is required.");
            }

            if (!LooksLikeSteamRoot(normalizedPath))
            {
                throw new InvalidOperationException("Choose the Steam folder that contains steam.exe, userdata, or config.");
            }

            var configuration = _settingsStore.Load();
            configuration.ManualOverridePath = normalizedPath;
            _settingsStore.Save(configuration);
            return BuildState(configuration);
        }
    }

    public SteamPathState ClearManualOverridePath()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            configuration.ManualOverridePath = string.Empty;
            _settingsStore.Save(configuration);
            return BuildState(configuration);
        }
    }

    public string? ResolveSteamRootPath()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            return ResolveSteamRootPath(configuration);
        }
    }

    public string? ResolveSteamExecutablePath()
    {
        lock (_gate)
        {
            var configuration = _settingsStore.Load();
            return ResolveSteamExecutablePath(configuration);
        }
    }

    private SteamPathState BuildState(SteamInstallPathConfiguration configuration)
    {
        var manualOverridePath = NormalizeSteamRootCandidate(configuration.ManualOverridePath) ?? string.Empty;
        var autoDetectedPath = ResolveAutoDetectedSteamRootPath() ?? string.Empty;
        var effectivePath = ResolveSteamRootPath(configuration) ?? string.Empty;

        return new SteamPathState(
            EffectivePath: effectivePath,
            AutoDetectedPath: autoDetectedPath,
            ManualOverridePath: manualOverridePath,
            UsingManualOverride: !string.IsNullOrWhiteSpace(manualOverridePath) &&
                PathsEqual(effectivePath, manualOverridePath));
    }

    private string? ResolveAutoDetectedSteamRootPath()
    {
        return EnumerateCandidateSteamRoots(manualOverridePath: null)
            .FirstOrDefault(LooksLikeSteamRoot);
    }

    private string? ResolveSteamRootPath(SteamInstallPathConfiguration configuration)
    {
        return EnumerateCandidateSteamRoots(configuration.ManualOverridePath)
            .FirstOrDefault(LooksLikeSteamRoot);
    }

    private string? ResolveSteamExecutablePath(SteamInstallPathConfiguration configuration)
    {
        return EnumerateCandidateSteamExecutablePaths(configuration.ManualOverridePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private IEnumerable<string> EnumerateCandidateSteamRoots(string? manualOverridePath)
    {
        var candidates = new[]
        {
            manualOverridePath,
            GetRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
            GetRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            GetRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            GetRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            _fallbackSteamRootPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };

        return candidates
            .Select(NormalizeSteamRootCandidate)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private IEnumerable<string> EnumerateCandidateSteamExecutablePaths(string? manualOverridePath)
    {
        var candidates = new[]
        {
            GetRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
            CombineSteamExecutablePath(GetRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")),
            CombineSteamExecutablePath(GetRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")),
            CombineSteamExecutablePath(GetRegistryString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")),
        }
        .Concat(EnumerateCandidateSteamRoots(manualOverridePath)
            .Select(CombineSteamExecutablePath));

        return candidates
            .Select(NormalizePathCandidate)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static string? NormalizeSteamRootCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var trimmedPath = path.Trim().Trim('"');
            if (trimmedPath.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                trimmedPath = Path.GetDirectoryName(trimmedPath) ?? trimmedPath;
            }

            return Path.GetFullPath(trimmedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePathCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeSteamRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "steam.exe")) ||
            Directory.Exists(Path.Combine(path, "userdata")) ||
            Directory.Exists(Path.Combine(path, "config"));
    }

    private static string? CombineSteamExecutablePath(string? rootPath)
    {
        var normalizedRoot = NormalizeSteamRootCandidate(rootPath);
        return string.IsNullOrWhiteSpace(normalizedRoot)
            ? null
            : Path.Combine(normalizedRoot, "steam.exe");
    }

    private static string? GetRegistryString(RegistryKey root, string keyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var normalizedLeft = NormalizeSteamRootCandidate(left);
        var normalizedRight = NormalizeSteamRootCandidate(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
            !string.IsNullOrWhiteSpace(normalizedRight) &&
            string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}
