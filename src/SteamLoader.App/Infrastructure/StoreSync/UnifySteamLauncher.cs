using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Handles `ToolsForSteam.exe --unifysteam-launch &lt;store&gt;:&lt;id&gt;` invocations coming
/// from the Storefront Steam shortcuts. Installs the game on first launch (visible
/// console progress) and starts it afterwards, keeping this process alive while the
/// game runs so Steam status and overlay behave correctly.
/// </summary>
internal static class UnifySteamLauncher
{
    public static int Run(string target)
    {
        try
        {
            var separatorIndex = (target ?? string.Empty).IndexOf(':');
            if (string.IsNullOrWhiteSpace(target) || separatorIndex <= 0 || separatorIndex >= target.Length - 1)
            {
                ShowError("The Storefront launch target is invalid. Run a Store Sync so the shortcut gets repaired.");
                return 1;
            }

            var storeId = target[..separatorIndex].Trim();
            var gameId = target[(separatorIndex + 1)..].Trim();
            if (!IsSafeLauncherId(gameId))
            {
                ShowError("The Storefront game ID is invalid. Run Store Sync again so the shortcut gets repaired.");
                return 1;
            }

            return storeId.ToLowerInvariant() switch
            {
                "epic-games" => RunEpic(gameId),
                "gog-galaxy" => RunGog(gameId),
                _ => Fail($"Unknown Storefront store '{storeId}'."),
            };
        }
        catch (Exception exception)
        {
            ShowError($"The Storefront launch failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunEpic(string appName)
    {
        var legendary = FindTool("legendary.exe", "legendary");
        if (string.IsNullOrWhiteSpace(legendary))
        {
            var epicLauncher = FindEpicGamesLauncherPath();
            if (!string.IsNullOrWhiteSpace(epicLauncher))
            {
                return RunEpicGamesLauncher(epicLauncher, appName);
            }

            return Fail("Epic Games Launcher or legendary was not found. Install Epic Games Launcher, Heroic, or legendary, then refresh Storefront.");
        }

        if (!IsEpicGameInstalled(legendary, appName))
        {
            // Visible console so the user sees the download progress.
            var installExitCode = RunVisibleAndWait(legendary, ["-y", "install", appName]);
            if (installExitCode != 0)
            {
                return Fail("The Epic download did not finish. Check the legendary window output and try again.");
            }
        }

        var executablePath = TryGetEpicExecutablePath(legendary, appName);

        // legendary handles tokens, EOS overlay and cloud saves during launch.
        RunVisibleAndWait(legendary, ["launch", appName], waitForExit: false);

        // Keep this process alive while the game runs so Steam shows it as running.
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            WaitForProcessByPath(executablePath);
        }

        return 0;
    }

    private static int RunEpicGamesLauncher(string epicLauncher, string appName)
    {
        var launchUri = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";
        if (TryOpenShellTarget(launchUri))
        {
            return 0;
        }

        RunVisibleAndWait(epicLauncher, [launchUri], waitForExit: false);
        return 0;
    }

    private static int RunGog(string gameId)
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolsForSteam",
            "UnifySteamGames");
        Directory.CreateDirectory(baseDirectory);

        var installRoot = FindKnownGogInstallRoot(baseDirectory, gameId);
        var executablePath = ResolveGogExecutablePath(installRoot, gameId);
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            return RunGogExecutable(executablePath, installRoot);
        }

        var gogdl = FindTool("gogdl.exe", "gogdl");
        var authPath = string.IsNullOrWhiteSpace(gogdl) ? string.Empty : FindGogAuthPath();
        if (!string.IsNullOrWhiteSpace(gogdl) && !string.IsNullOrWhiteSpace(authPath))
        {
            // gogdl still gives us a direct install path when Heroic/gogdl is configured,
            // but it is no longer required when GOG Galaxy is available.
            var downloadExitCode = RunVisibleAndWait(
                gogdl,
                ["--auth-config-path", authPath, "download", gameId, "--platform", "windows", "--path", baseDirectory]);
            if (downloadExitCode != 0)
            {
                return Fail("The GOG download did not finish. Check the gogdl window output and try again.");
            }

            installRoot = FindKnownGogInstallRoot(baseDirectory, gameId);
            executablePath = ResolveGogExecutablePath(installRoot, gameId);
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                return RunGogExecutable(executablePath, installRoot);
            }
        }

        var galaxyClient = FindGogGalaxyClientPath();
        if (!string.IsNullOrWhiteSpace(galaxyClient))
        {
            return !string.IsNullOrWhiteSpace(installRoot)
                ? RunGogGalaxy(galaxyClient, gameId, installRoot, executablePath)
                : OpenGogGalaxyGameView(galaxyClient, gameId);
        }

        if (string.IsNullOrWhiteSpace(gogdl))
        {
            return Fail("GOG Galaxy or gogdl was not found. Install GOG Galaxy, Heroic, or gogdl, then refresh Storefront.");
        }

        if (string.IsNullOrWhiteSpace(authPath))
        {
            return Fail("No GOG sign-in data was found. Sign in to GOG in Storefront first or install GOG Galaxy.");
        }

        return Fail("The GOG game executable could not be resolved. Refresh Storefront or start the title once in GOG Galaxy.");
    }

    private static int RunGogExecutable(string executablePath, string? installRoot)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? installRoot ?? string.Empty,
            UseShellExecute = true,
        });
        process?.WaitForExit();
        return 0;
    }

    private static int RunGogGalaxy(string galaxyClient, string gameId, string? installRoot, string executablePath)
    {
        var arguments = new List<string>
        {
            "/command=runGame",
            $"/gameId={gameId}",
        };

        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            arguments.Add($"/path={installRoot}");
        }

        RunVisibleAndWait(galaxyClient, arguments, waitForExit: false);

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            WaitForProcessByPath(executablePath);
        }

        return 0;
    }

    private static int OpenGogGalaxyGameView(string galaxyClient, string gameId)
    {
        var gameViewUrl = $"goggalaxy://openGameView/{gameId}";
        if (TryOpenShellTarget(gameViewUrl))
        {
            return 0;
        }

        // If the protocol registration is stale, invoke Galaxy's protocol bridge directly.
        RunVisibleAndWait(galaxyClient, [$"/urlProtocol={gameViewUrl}"], waitForExit: false);
        return 0;
    }

    private static bool IsEpicGameInstalled(string legendary, string appName)
    {
        try
        {
            var output = RunHiddenAndCapture(legendary, "list-installed", "--json");
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("app_name", out var appNameNode) &&
                    string.Equals(appNameNode.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to install; legendary install verifies existing files anyway.
        }

        return false;
    }

    private static string TryGetEpicExecutablePath(string legendary, string appName)
    {
        try
        {
            var output = RunHiddenAndCapture(legendary, "list-installed", "--json");
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("app_name", out var appNameNode) ||
                    !string.Equals(appNameNode.GetString(), appName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installPath = item.TryGetProperty("install_path", out var installPathNode)
                    ? installPathNode.GetString() ?? string.Empty
                    : string.Empty;
                var executable = item.TryGetProperty("executable", out var executableNode)
                    ? executableNode.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return string.Empty;
                }

                return Path.IsPathRooted(executable)
                    ? executable
                    : Path.Combine(installPath, executable);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string? FindGogInstallRoot(string baseDirectory, string gameId)
    {
        try
        {
            var infoFileName = $"goggame-{gameId}.info";
            var match = Directory
                .EnumerateFiles(baseDirectory, infoFileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            return match is null ? null : Path.GetDirectoryName(match);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveGogPrimaryExecutable(string installRoot, string gameId)
    {
        try
        {
            var infoPath = Path.Combine(installRoot, $"goggame-{gameId}.info");
            if (!File.Exists(infoPath))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
            if (!document.RootElement.TryGetProperty("playTasks", out var playTasks) ||
                playTasks.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            JsonElement? fallbackTask = null;
            foreach (var task in playTasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var hasPath = task.TryGetProperty("path", out var pathNode) &&
                              !string.IsNullOrWhiteSpace(pathNode.GetString());
                if (!hasPath)
                {
                    continue;
                }

                fallbackTask ??= task;
                if (task.TryGetProperty("isPrimary", out var isPrimaryNode) &&
                    isPrimaryNode.ValueKind == JsonValueKind.True)
                {
                    return Path.Combine(installRoot, pathNode.GetString()!.Replace('/', '\\'));
                }
            }

            if (fallbackTask is { } fallback &&
                fallback.TryGetProperty("path", out var fallbackPath))
            {
                return Path.Combine(installRoot, fallbackPath.GetString()!.Replace('/', '\\'));
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string? FindKnownGogInstallRoot(string baseDirectory, string gameId)
    {
        return FindGogInstallRoot(baseDirectory, gameId)
               ?? FindGogRegistryInstallRoot(gameId);
    }

    private static string? FindGogRegistryInstallRoot(string gameId)
    {
        try
        {
            foreach (var root in OpenGogGameRegistryRoots())
            {
                using (root)
                {
                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var installPath = NormalizeLoosePath(
                            GetRegistryString(gameKey, "path")
                            ?? GetRegistryString(gameKey, "PATH")
                            ?? GetRegistryString(gameKey, "InstallLocation")
                            ?? string.Empty);

                        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                        {
                            continue;
                        }

                        if (RegistryKeyMatchesGogGame(gameKey, subKeyName, gameId) ||
                            File.Exists(Path.Combine(installPath, $"goggame-{gameId}.info")))
                        {
                            return installPath;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveGogExecutablePath(string? installRoot, string gameId)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
        {
            return string.Empty;
        }

        var manifestExecutable = ResolveGogPrimaryExecutable(installRoot, gameId);
        if (!string.IsNullOrWhiteSpace(manifestExecutable) && File.Exists(manifestExecutable))
        {
            return manifestExecutable;
        }

        var registryHint = FindGogRegistryExecutableHint(gameId, installRoot);
        var registryExecutable = ResolveExecutableHint(installRoot, registryHint);
        if (!string.IsNullOrWhiteSpace(registryExecutable) && File.Exists(registryExecutable))
        {
            return registryExecutable;
        }

        return FindBestExecutable(installRoot);
    }

    private static string FindGogRegistryExecutableHint(string gameId, string installRoot)
    {
        try
        {
            foreach (var root in OpenGogGameRegistryRoots())
            {
                using (root)
                {
                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        using var gameKey = root.OpenSubKey(subKeyName);
                        if (gameKey is null)
                        {
                            continue;
                        }

                        var keyInstallPath = NormalizeLoosePath(
                            GetRegistryString(gameKey, "path")
                            ?? GetRegistryString(gameKey, "PATH")
                            ?? GetRegistryString(gameKey, "InstallLocation")
                            ?? string.Empty);

                        var installPathMatches = !string.IsNullOrWhiteSpace(keyInstallPath) &&
                                                 string.Equals(
                                                     Path.GetFullPath(keyInstallPath),
                                                     Path.GetFullPath(installRoot),
                                                     StringComparison.OrdinalIgnoreCase);
                        if (!installPathMatches && !RegistryKeyMatchesGogGame(gameKey, subKeyName, gameId))
                        {
                            continue;
                        }

                        return GetRegistryString(gameKey, "exe")
                               ?? GetRegistryString(gameKey, "gameExe")
                               ?? GetRegistryString(gameKey, "launchCommand")
                               ?? string.Empty;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static IEnumerable<RegistryKey> OpenGogGameRegistryRoots()
    {
        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\WOW6432Node\GOG.com\Games",
                     @"SOFTWARE\GOG.com\Games",
                 })
        {
            var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static bool RegistryKeyMatchesGogGame(RegistryKey gameKey, string subKeyName, string gameId)
    {
        if (string.Equals(subKeyName, gameId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var valueName in new[] { "gameID", "gameId", "productID", "productId" })
        {
            if (string.Equals(GetRegistryString(gameKey, valueName), gameId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveExecutableHint(string installRoot, string executableHint)
    {
        if (string.IsNullOrWhiteSpace(executableHint))
        {
            return string.Empty;
        }

        var extractedPath = ExtractRegistryExecutablePath(executableHint);
        if (string.IsNullOrWhiteSpace(extractedPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(extractedPath)
            ? NormalizeLoosePath(extractedPath)
            : NormalizeLoosePath(Path.Combine(installRoot, extractedPath));
    }

    private static string FindBestExecutable(string installRoot)
    {
        try
        {
            var rootName = NormalizeExecutableName(Path.GetFileName(installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            return Directory
                .EnumerateFiles(installRoot, "*.exe", SearchOption.AllDirectories)
                .Where(path => !ShouldIgnoreGogExecutable(path))
                .OrderByDescending(path => NormalizeExecutableName(Path.GetFileNameWithoutExtension(path)).Contains(rootName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ShouldIgnoreGogExecutable(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("vcredist", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("vc_redist", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("dxsetup", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("directx", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("galaxy", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutableName(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    }

    private static void WaitForProcessByPath(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        var processName = Path.GetFileNameWithoutExtension(normalized);

        // Give the launcher a moment to spawn the game, then wait for the game to exit.
        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            var process = Process
                .GetProcessesByName(processName)
                .FirstOrDefault(candidate =>
                {
                    try
                    {
                        return string.Equals(
                            Path.GetFullPath(candidate.MainModule?.FileName ?? string.Empty),
                            normalized,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (process is not null)
            {
                using (process)
                {
                    process.WaitForExit();
                }

                return;
            }

            Thread.Sleep(2000);
        }
    }

    private static int RunVisibleAndWait(string toolPath, IReadOnlyList<string> arguments, bool waitForExit = true)
    {
        using var process = Process.Start(CreateStartInfo(toolPath, arguments, visible: true, redirectOutput: false));

        if (process is null)
        {
            return 1;
        }

        if (!waitForExit)
        {
            return 0;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool TryOpenShellTarget(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string RunHiddenAndCapture(string toolPath, params string[] arguments)
    {
        using var process = Process.Start(CreateStartInfo(toolPath, arguments, visible: false, redirectOutput: true));

        if (process is null)
        {
            return string.Empty;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return output;
    }

    private static ProcessStartInfo CreateStartInfo(
        string toolPath,
        IReadOnlyList<string> arguments,
        bool visible,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = !visible,
        };

        if (IsBatchLike(toolPath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"\"{toolPath}\" {JoinCommandLine(arguments)}");
            return startInfo;
        }

        startInfo.FileName = toolPath;
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string JoinCommandLine(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    private static bool IsBatchLike(string toolPath)
    {
        var extension = Path.GetExtension(toolPath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeLauncherId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(character =>
                   char.IsLetterOrDigit(character) ||
                   character is '_' or '-' or '.');
    }

    private static string FindTool(string executableName, string commandName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in new[] { executableName, commandName, commandName + ".exe", commandName + ".cmd", commandName + ".bat" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var heroicBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "heroic",
            "resources",
            "app.asar.unpacked",
            "build",
            "bin",
            "win32");
        foreach (var name in new[] { executableName, commandName })
        {
            var candidate = Path.Combine(heroicBase, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string FindGogGalaxyClientPath()
    {
        var candidates = GetGogGalaxyClientCandidates()
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindTool("GalaxyClient.exe", "GalaxyClient");
    }

    private static string FindEpicGamesLauncherPath()
    {
        foreach (var candidate in GetEpicGamesLauncherCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindTool("EpicGamesLauncher.exe", "EpicGamesLauncher");
    }

    private static IEnumerable<string> GetEpicGamesLauncherCandidates()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
            yield return Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe");
        }
    }

    private static IEnumerable<string> GetGogGalaxyClientCandidates()
    {
        foreach (var candidate in GetGogGalaxyRegistryCandidates())
        {
            yield return candidate;
        }

        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty,
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty,
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            yield return Path.Combine(folder, "GOG Galaxy", "GalaxyClient.exe");
            yield return Path.Combine(folder, "GOG.com", "Galaxy", "GalaxyClient.exe");
        }
    }

    private static IEnumerable<string> GetGogGalaxyRegistryCandidates()
    {
        foreach (var root in OpenUninstallRegistryRoots())
        {
            using (root)
            {
                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var appKey = root.OpenSubKey(subKeyName);
                    if (appKey is null)
                    {
                        continue;
                    }

                    var displayName = GetRegistryString(appKey, "DisplayName") ?? string.Empty;
                    if (!displayName.Contains("GOG", StringComparison.OrdinalIgnoreCase) ||
                        !displayName.Contains("GALAXY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var installLocation = NormalizeLoosePath(GetRegistryString(appKey, "InstallLocation") ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return Path.Combine(installLocation, "GalaxyClient.exe");
                    }

                    foreach (var executableValue in new[]
                             {
                                 GetRegistryString(appKey, "DisplayIcon"),
                                 GetRegistryString(appKey, "UninstallString"),
                             })
                    {
                        var extractedPath = ExtractRegistryExecutablePath(executableValue ?? string.Empty);
                        var directory = string.IsNullOrWhiteSpace(extractedPath)
                            ? string.Empty
                            : Path.GetDirectoryName(extractedPath) ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            yield return Path.Combine(directory, "GalaxyClient.exe");
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<RegistryKey> OpenUninstallRegistryRoots()
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            var key = hive.OpenSubKey(path);
            if (key is not null)
            {
                yield return key;
            }
        }
    }

    private static string FindGogAuthPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var candidate in new[]
                 {
                     Path.Combine(appData, "heroic", "gog_store", "auth.json"),
                     Path.Combine(appData, "heroic_gogdl", "auth.json"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string? GetRegistryString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) as string;
    }

    private static string ExtractRegistryExecutablePath(string value)
    {
        var trimmed = NormalizeLoosePath(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1
                ? NormalizeLoosePath(trimmed[1..closingQuote])
                : trimmed.Trim('"');
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0
            ? NormalizeLoosePath(trimmed[..(exeIndex + 4)])
            : trimmed;
    }

    private static string NormalizeLoosePath(string value)
    {
        var trimmed = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static int Fail(string message)
    {
        ShowError(message);
        return 1;
    }

    private static void ShowError(string message)
    {
        System.Windows.MessageBox.Show(
            message,
            "Storefront",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
