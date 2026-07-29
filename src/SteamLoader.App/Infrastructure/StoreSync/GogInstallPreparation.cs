using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLoader.App.Infrastructure.Helpers;

namespace SteamLoader.App.Infrastructure.StoreSync;

/// <summary>
/// Completes the Windows setup phase described by gogdl's authenticated manifest.
/// Downloaded game files alone are not sufficient for some GOG titles: the
/// publisher setup command and common redistributables may still be required.
/// </summary>
internal static class GogInstallPreparation
{
    public const string ElevatedArgument =
        "--unifysteam-gog-prepare-elevated";
    private const string SetupMarkerFileName =
        ".tools-for-steam-omnilibrary-gog-setup-v1";
    private const string SetupSchemaVersion = "2";
    private const string ManagedInstallMarkerFileName =
        ".tools-for-steam-omnilibrary-gog";
    private static readonly Regex SafeIdentifierPattern =
        new(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    internal sealed record SetupCommand(
        string ProductId,
        string ExecutableName,
        bool UsesScriptInterpreter);

    internal sealed record SetupPlan(
        string Signature,
        string BuildId,
        string LanguageCode,
        string LanguageName,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<SetupCommand> Commands);

    private sealed record ElevatedResult(
        bool Success,
        string Error);

    public static bool EnsureReady(
        string gogdlPath,
        string authPath,
        string installRoot,
        string gameId,
        Action<string>? log,
        out string error)
    {
        error = string.Empty;
        try
        {
            var plan = ResolvePlan(
                ManagedGogDlHelper.RuntimeConfigPath,
                installRoot,
                gameId);
            if (plan is null)
            {
                error =
                    "The authenticated GOG setup manifest is missing. Reconnect GOG and retry the download.";
                return false;
            }

            var markerPath = Path.Combine(installRoot, SetupMarkerFileName);
            if (File.Exists(markerPath) &&
                string.Equals(
                    File.ReadAllText(markerPath).Trim(),
                    plan.Signature,
                    StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("GOG Windows setup already matches the installed build.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(gogdlPath) || !File.Exists(gogdlPath))
            {
                error = "The managed GOG helper is unavailable.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authPath) || !File.Exists(authPath))
            {
                error = "The isolated GOG sign-in is unavailable. Connect GOG again.";
                return false;
            }

            Directory.CreateDirectory(ManagedGogDlHelper.RedistDirectory);
            Directory.CreateDirectory(ManagedGogDlHelper.GetSupportDirectory(gameId));

            if (plan.Dependencies.Count > 0 &&
                !DownloadRedistributables(
                    gogdlPath,
                    authPath,
                    plan.Dependencies,
                    log,
                    out error))
            {
                return false;
            }

            if (plan.Commands.Count == 0 && plan.Dependencies.Count == 0)
            {
                File.WriteAllText(markerPath, plan.Signature);
                log?.Invoke("GOG Windows setup did not require elevated actions.");
                return true;
            }

            if (!RunElevatedPreparation(
                    plan,
                    installRoot,
                    gameId,
                    log,
                    out error))
            {
                return false;
            }

            File.WriteAllText(markerPath, plan.Signature);
            log?.Invoke(
                $"GOG Windows setup completed build={plan.BuildId} dependencies={plan.Dependencies.Count}");
            return true;
        }
        catch (Exception exception)
        {
            error = DescribeFailure(exception);
            return false;
        }
    }

    public static int RunElevated(
        string gameId,
        string installRoot,
        string expectedSignature,
        string resultToken)
    {
        var success = false;
        var error = string.Empty;
        try
        {
            if (!ElevatedHelperTaskService.IsCurrentProcessElevated())
            {
                error = "The GOG setup worker did not receive administrator rights.";
                return 1;
            }

            if (!SafeIdentifierPattern.IsMatch(gameId) ||
                !Regex.IsMatch(
                    expectedSignature ?? string.Empty,
                    "^[A-Fa-f0-9]{64}$",
                    RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(
                    resultToken ?? string.Empty,
                    "^[A-Fa-f0-9]{32}$",
                    RegexOptions.CultureInvariant))
            {
                error = "The GOG setup worker received an invalid request.";
                return 1;
            }

            installRoot = Path.GetFullPath(installRoot);
            if (!Directory.Exists(installRoot) ||
                !File.Exists(Path.Combine(installRoot, $"goggame-{gameId}.info")) ||
                !IsManagedInstall(installRoot, gameId))
            {
                error = "The GOG setup worker rejected an unmanaged installation path.";
                return 1;
            }

            var plan = ResolvePlan(
                ManagedGogDlHelper.RuntimeConfigPath,
                installRoot,
                gameId);
            if (plan is null ||
                !string.Equals(
                    plan.Signature,
                    expectedSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The GOG setup manifest changed before elevation. Retry the operation.";
                return 1;
            }

            if (!RunPublisherSetup(
                    plan,
                    installRoot,
                    gameId,
                    message => UnifySteamLauncher.WriteGogLaunchLog(gameId, message),
                    out error))
            {
                return 1;
            }

            if (plan.Dependencies.Count > 0 &&
                !InstallRedistributables(
                    plan.Dependencies,
                    message => UnifySteamLauncher.WriteGogLaunchLog(gameId, message),
                    out error))
            {
                return 1;
            }

            success = true;
            return 0;
        }
        catch (Exception exception)
        {
            error = DescribeFailure(exception);
            return 1;
        }
        finally
        {
            WriteElevatedResult(resultToken, success, error);
        }
    }

    internal static SetupPlan? ResolvePlan(
        string runtimeConfigPath,
        string installRoot,
        string gameId)
    {
        if (!SafeIdentifierPattern.IsMatch(gameId))
        {
            return null;
        }

        var manifestPath = Path.Combine(
            runtimeConfigPath,
            "heroic_gogdl",
            "manifests",
            gameId);
        if (!File.Exists(manifestPath) || !Directory.Exists(installRoot))
        {
            return null;
        }

        var manifestText = File.ReadAllText(manifestPath);
        using var document = JsonDocument.Parse(manifestText);
        var root = document.RootElement;
        var version = ReadInt32(root, "version");
        var buildId = ReadString(root, "buildId");
        var languageCode = ReadString(root, "HGLInstallLanguage");
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            languageCode = "en-US";
        }

        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new List<SetupCommand>();
        if (version == 2)
        {
            if (root.TryGetProperty("dependencies", out var dependencyNodes) &&
                dependencyNodes.ValueKind == JsonValueKind.Array)
            {
                AddSafeIdentifiers(dependencies, dependencyNodes);
            }

            var usesScriptInterpreter =
                ReadBoolean(root, "scriptInterpreter") ||
                ReadBoolean(root, "script_interpreter");
            if (usesScriptInterpreter)
            {
                dependencies.Add("ISI");
            }

            if (root.TryGetProperty("products", out var products) &&
                products.ValueKind == JsonValueKind.Array)
            {
                foreach (var product in products.EnumerateArray())
                {
                    var productId = ReadString(product, "productId");
                    if (!string.Equals(productId, gameId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (usesScriptInterpreter)
                    {
                        commands.Add(new SetupCommand(productId, string.Empty, true));
                        continue;
                    }

                    var executableName = ReadString(product, "temp_executable");
                    if (!string.IsNullOrWhiteSpace(executableName))
                    {
                        if (!IsSafeRelativePath(executableName))
                        {
                            return null;
                        }

                        commands.Add(new SetupCommand(productId, executableName, false));
                    }
                }
            }
        }
        else if (version == 1 &&
                 root.TryGetProperty("product", out var product) &&
                 product.ValueKind == JsonValueKind.Object)
        {
            buildId = ReadString(product, "timestamp");
            if (product.TryGetProperty("depots", out var depots) &&
                depots.ValueKind == JsonValueKind.Array)
            {
                foreach (var depot in depots.EnumerateArray())
                {
                    var dependency = ReadString(depot, "redist");
                    if (SafeIdentifierPattern.IsMatch(dependency))
                    {
                        dependencies.Add(dependency);
                    }
                }
            }

            if (product.TryGetProperty("support_commands", out var supportCommands) &&
                supportCommands.ValueKind == JsonValueKind.Array)
            {
                foreach (var supportCommand in supportCommands.EnumerateArray())
                {
                    if (!MatchesLanguage(supportCommand, languageCode))
                    {
                        continue;
                    }

                    var productId = ReadString(supportCommand, "gameID");
                    var executableName = ReadString(supportCommand, "executable");
                    if (!string.IsNullOrWhiteSpace(executableName) &&
                        (!SafeIdentifierPattern.IsMatch(productId) ||
                         !IsSafeRelativePath(executableName)))
                    {
                        return null;
                    }

                    if (SafeIdentifierPattern.IsMatch(productId) &&
                        !string.IsNullOrWhiteSpace(executableName))
                    {
                        commands.Add(new SetupCommand(productId, executableName, false));
                    }
                }
            }
        }
        else
        {
            return null;
        }

        var signature = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{SetupSchemaVersion}\n{manifestText}")));
        return new SetupPlan(
            signature,
            string.IsNullOrWhiteSpace(buildId) ? "unknown" : buildId,
            languageCode,
            GetEnglishLanguageName(languageCode),
            dependencies.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            commands);
    }

    private static bool DownloadRedistributables(
        string gogdlPath,
        string authPath,
        IReadOnlyList<string> dependencies,
        Action<string>? log,
        out string error)
    {
        error = string.Empty;
        log?.Invoke($"downloading GOG dependencies count={dependencies.Count}");
        var startInfo = new ProcessStartInfo
        {
            FileName = gogdlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--auth-config-path");
        startInfo.ArgumentList.Add(authPath);
        startInfo.ArgumentList.Add("redist");
        startInfo.ArgumentList.Add("--ids");
        startInfo.ArgumentList.Add(string.Join(',', dependencies));
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(ManagedGogDlHelper.RedistDirectory);
        startInfo.ArgumentList.Add("--max-workers");
        startInfo.ArgumentList.Add("4");
        ManagedGogDlHelper.ConfigureEnvironment(startInfo);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            error = "gogdl did not start the dependency download.";
            return false;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        if (process.ExitCode == 0)
        {
            return true;
        }

        var diagnostic = FirstUsefulLine(errorTask.Result, outputTask.Result);
        error = string.IsNullOrWhiteSpace(diagnostic)
            ? $"GOG dependency download failed with exit code {process.ExitCode}."
            : $"GOG dependency download failed: {diagnostic}";
        return false;
    }

    private static bool RunElevatedPreparation(
        SetupPlan plan,
        string installRoot,
        string gameId,
        Action<string>? log,
        out string error)
    {
        error = string.Empty;
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            error = "The TFS GOG setup worker is unavailable.";
            return false;
        }

        var resultToken = Guid.NewGuid().ToString("N");
        var resultPath = GetElevatedResultPath(resultToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }

            var arguments = new[]
            {
                ElevatedArgument,
                gameId,
                Path.GetFullPath(installRoot),
                plan.Signature,
                resultToken,
            };
            log?.Invoke(
                $"requesting one elevated GOG setup worker actions={plan.Commands.Count} dependencies={plan.Dependencies.Count}");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = string.Join(' ', arguments.Select(QuoteArgument)),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                error = "The elevated TFS GOG setup worker did not start.";
                return false;
            }

            process.WaitForExit();
            ElevatedResult? result = null;
            if (File.Exists(resultPath))
            {
                result = JsonSerializer.Deserialize<ElevatedResult>(
                    File.ReadAllText(resultPath));
            }

            if (process.ExitCode == 0 && result?.Success == true)
            {
                log?.Invoke("single elevated GOG setup worker completed");
                return true;
            }

            error = !string.IsNullOrWhiteSpace(result?.Error)
                ? result.Error
                : $"The elevated TFS GOG setup worker exited with code {process.ExitCode}.";
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            error = "Windows administrator approval was canceled.";
            return false;
        }
        catch (Exception exception)
        {
            error = DescribeFailure(exception);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch
            {
            }
        }
    }

    private static bool RunPublisherSetup(
        SetupPlan plan,
        string installRoot,
        string gameId,
        Action<string>? log,
        out string error)
    {
        error = string.Empty;
        foreach (var command in plan.Commands)
        {
            var executablePath = command.UsesScriptInterpreter
                ? ResolvePathWithinDirectory(
                    ManagedGogDlHelper.RedistDirectory,
                    Path.Combine("__redist", "ISI", "scriptinterpreter.exe"))
                : FindPublisherSetupExecutable(
                    installRoot,
                    ManagedGogDlHelper.GetSupportDirectory(gameId),
                    command.ProductId,
                    command.ExecutableName);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                error = command.UsesScriptInterpreter
                    ? "GOG requires its script interpreter, but the authenticated dependency download did not provide it."
                    : "GOG's authenticated publisher setup file is missing. Retry the game download.";
                return false;
            }

            var arguments = BuildPublisherSetupArguments(
                plan,
                command,
                installRoot,
                ManagedGogDlHelper.GetSupportDirectory(gameId));
            log?.Invoke(
                $"running GOG publisher setup product={command.ProductId} interpreter={command.UsesScriptInterpreter}");
            if (!RunSetupProcessAndWait(
                    executablePath,
                    string.Join(' ', arguments.Select(QuoteArgument)),
                    installRoot,
                    out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InstallRedistributables(
        IReadOnlyList<string> dependencies,
        Action<string>? log,
        out string error)
    {
        error = string.Empty;
        var manifestPath = Path.Combine(
            ManagedGogDlHelper.RedistDirectory,
            ".gogdl-redist-manifest");
        if (!File.Exists(manifestPath))
        {
            error = "GOG finished without a redistributable manifest.";
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("depots", out var depots) ||
            depots.ValueKind != JsonValueKind.Array)
        {
            error = "GOG returned an invalid redistributable manifest.";
            return false;
        }

        var repositoryBuildId = ReadString(root, "build_id");
        var requested = new HashSet<string>(dependencies, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var depot in depots.EnumerateArray())
        {
            var dependencyId = ReadString(depot, "dependencyId");
            if (!requested.Contains(dependencyId))
            {
                continue;
            }

            found.Add(dependencyId);
            if (!depot.TryGetProperty("executable", out var executable) ||
                executable.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var relativeExecutablePath = ReadString(executable, "path");
            if (string.IsNullOrWhiteSpace(relativeExecutablePath) ||
                !relativeExecutablePath.Replace('\\', '/')
                    .StartsWith("__redist/", StringComparison.OrdinalIgnoreCase))
            {
                // Game-local dependencies are downloaded into the game directory
                // by gogdl itself and do not have a global installer phase.
                continue;
            }

            var executablePath = ResolvePathWithinDirectory(
                ManagedGogDlHelper.RedistDirectory,
                relativeExecutablePath);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                error = $"GOG dependency '{dependencyId}' was not downloaded correctly.";
                return false;
            }

            var dependencySignature =
                $"{repositoryBuildId}:{ReadString(depot, "manifest")}:{SetupSchemaVersion}";
            var markerPath = GetDependencyMarkerPath(dependencyId);
            if (File.Exists(markerPath) &&
                string.Equals(
                    File.ReadAllText(markerPath).Trim(),
                    dependencySignature,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var rawArguments = ReadString(executable, "arguments");
            string toolPath;
            string arguments;
            if (string.Equals(
                    Path.GetExtension(executablePath),
                    ".msi",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dependencyId, "PHYSXLEGACY", StringComparison.OrdinalIgnoreCase))
            {
                toolPath = "msiexec.exe";
                arguments = $"/i {QuoteArgument(executablePath)} {rawArguments} /qb";
            }
            else
            {
                toolPath = executablePath;
                arguments = rawArguments;
            }

            log?.Invoke($"installing GOG dependency id={dependencyId}");
            if (!RunSetupProcessAndWait(
                    toolPath,
                    arguments,
                    Path.GetDirectoryName(executablePath) ??
                    ManagedGogDlHelper.RedistDirectory,
                    out error))
            {
                error = $"{dependencyId}: {error}";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, dependencySignature);
        }

        var unresolved = requested
            .Where(dependency => !found.Contains(dependency))
            .ToArray();
        if (unresolved.Length > 0)
        {
            error =
                $"GOG did not describe the required dependencies: {string.Join(", ", unresolved)}.";
            return false;
        }

        return true;
    }

    private static bool RunSetupProcessAndWait(
        string executablePath,
        string arguments,
        string workingDirectory,
        out string error)
    {
        error = string.Empty;
        if (!ElevatedHelperTaskService.IsCurrentProcessElevated())
        {
            error = "The GOG setup action was blocked because its worker is not elevated.";
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });
            if (process is null)
            {
                error = "The Windows setup process did not start.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode is 0 or 1641 or 3010)
            {
                return true;
            }

            error = $"The Windows setup process exited with code {process.ExitCode}.";
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            error = "Windows administrator approval was canceled.";
            return false;
        }
        catch (Exception exception)
        {
            error = DescribeFailure(exception);
            return false;
        }
    }

    private static IReadOnlyList<string> BuildPublisherSetupArguments(
        SetupPlan plan,
        SetupCommand command,
        string installRoot,
        string supportDirectory)
    {
        var arguments = new List<string>
        {
            "/VERYSILENT",
            $"/DIR={installRoot}",
            $"/Language={plan.LanguageName}",
            $"/LANG={plan.LanguageName}",
            $"/lang-code={plan.LanguageCode}",
            $"/ProductId={command.ProductId}",
            "/galaxyclient",
            $"/buildId={plan.BuildId}",
            "/versionName=",
        };
        if (command.UsesScriptInterpreter)
        {
            arguments.Add($"/supportDir={supportDirectory}");
        }

        // Both spellings are intentionally passed. Older GOG setup packages
        // shipped with the misspelled switch.
        arguments.Add("/nodesktopshorctut");
        arguments.Add("/nodesktopshortcut");
        return arguments;
    }

    private static string FindPublisherSetupExecutable(
        string installRoot,
        string supportDirectory,
        string productId,
        string executableName)
    {
        var candidates = new[]
        {
            ResolvePathWithinDirectory(
                supportDirectory,
                Path.Combine(productId, executableName)),
            ResolvePathWithinDirectory(supportDirectory, executableName),
            ResolvePathWithinDirectory(
                installRoot,
                Path.Combine("gog-support", productId, executableName)),
            ResolvePathWithinDirectory(
                installRoot,
                Path.Combine("gog-support", executableName)),
        };
        return candidates.FirstOrDefault(
                   path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
               ?? string.Empty;
    }

    private static string GetDependencyMarkerPath(string dependencyId)
    {
        if (!SafeIdentifierPattern.IsMatch(dependencyId))
        {
            throw new InvalidOperationException("GOG returned an invalid dependency identifier.");
        }

        return Path.Combine(
            ManagedGogDlHelper.RedistDirectory,
            ".tools-for-steam-installed",
            $"{dependencyId}.marker");
    }

    private static string GetElevatedResultPath(string resultToken)
    {
        if (!Regex.IsMatch(
                resultToken ?? string.Empty,
                "^[A-Fa-f0-9]{32}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "The GOG setup result token is invalid.");
        }

        return Path.Combine(
            ManagedGogDlHelper.ConfigDirectory,
            "setup-results",
            $"{resultToken}.json");
    }

    private static void WriteElevatedResult(
        string resultToken,
        bool success,
        string error)
    {
        try
        {
            var resultPath = GetElevatedResultPath(resultToken);
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    new ElevatedResult(
                        success,
                        string.IsNullOrWhiteSpace(error)
                            ? string.Empty
                            : DescribeFailure(new InvalidOperationException(error)))));
        }
        catch
        {
        }
    }

    private static bool IsManagedInstall(string installRoot, string gameId)
    {
        try
        {
            var markerPath = Path.Combine(
                installRoot,
                ManagedInstallMarkerFileName);
            return File.Exists(markerPath) &&
                   string.Equals(
                       File.ReadAllText(markerPath).Trim(),
                       gameId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddSafeIdentifiers(
        HashSet<string> target,
        JsonElement values)
    {
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var identifier = value.GetString() ?? string.Empty;
            if (SafeIdentifierPattern.IsMatch(identifier))
            {
                target.Add(identifier);
            }
        }
    }

    private static bool MatchesLanguage(JsonElement command, string languageCode)
    {
        if (!command.TryGetProperty("languages", out var languages) ||
            languages.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var twoLetterCode = languageCode.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? languageCode;
        return languages.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Any(value =>
                value is "*" ||
                value.Equals("Neutral", StringComparison.OrdinalIgnoreCase) ||
                value.Equals(languageCode, StringComparison.OrdinalIgnoreCase) ||
                value.Equals(twoLetterCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetEnglishLanguageName(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            var neutralCulture = culture.IsNeutralCulture
                ? culture
                : culture.Parent;
            var name = neutralCulture.EnglishName;
            return string.IsNullOrWhiteSpace(name)
                ? "english"
                : name.ToLowerInvariant();
        }
        catch
        {
            return "english";
        }
    }

    private static string ResolvePathWithinDirectory(
        string directory,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        try
        {
            var normalizedDirectory = Path.GetFullPath(directory);
            var candidate = Path.GetFullPath(
                Path.Combine(
                    normalizedDirectory,
                    relativePath.Replace(
                        Path.AltDirectorySeparatorChar,
                        Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(normalizedDirectory, candidate);
            return !Path.IsPathRooted(relative) &&
                   !string.Equals(relative, "..", StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       $"..{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                ? candidate
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               !Path.IsPathRooted(path) &&
               !path.Split(
                       [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                       StringSplitOptions.RemoveEmptyEntries)
                   .Any(segment => segment == "..");
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static int ReadInt32(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static string FirstUsefulLine(params string[] values)
    {
        var line = values
            .SelectMany(value => (value ?? string.Empty).Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries))
            .Select(value => Regex.Replace(value, @"\s+", " ").Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        return line.Length <= 500 ? line : line[..500];
    }

    private static string DescribeFailure(Exception exception)
    {
        return Regex.Replace(exception.Message ?? string.Empty, @"\s+", " ").Trim();
    }
}
