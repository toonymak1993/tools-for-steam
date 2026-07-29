using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal static class EpicInstallPreparation
{
    public static bool EnsureReady(
        string legendary,
        UnifySteamGameCacheEntry? cachedGame,
        string appName,
        out string completedSignature,
        out string error)
    {
        completedSignature = cachedGame?.PreparationSignature ?? string.Empty;
        error = string.Empty;
        try
        {
            var output = UnifySteamLauncher.RunHiddenAndCapture(
                legendary,
                "list-installed",
                "--json");
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(output) ? "[]" : output);
            var installed = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault(item =>
                    GetString(item, "app_name").Equals(
                        appName,
                        StringComparison.OrdinalIgnoreCase))
                : default;
            if (installed.ValueKind != JsonValueKind.Object ||
                !installed.TryGetProperty("prereq_info", out var prerequisite) ||
                prerequisite.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (prerequisite.TryGetProperty("installed", out var installedNode) &&
                installedNode.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            var installRoot = GetString(installed, "install_path");
            var relativePath = GetString(prerequisite, "path")
                .Replace('/', Path.DirectorySeparatorChar);
            var arguments = GetString(prerequisite, "args");
            var name = GetString(prerequisite, "name");
            if (!TryResolveContainedFile(
                    installRoot,
                    relativePath,
                    out var prerequisitePath))
            {
                error =
                    $"The required {FirstNonEmpty(name, "Windows prerequisite")} " +
                    "could not be found inside the completed game installation.";
                return false;
            }

            var extension = Path.GetExtension(prerequisitePath);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "The game declared an unsupported prerequisite type. " +
                    "Open the publisher launcher to finish setup safely.";
                return false;
            }

            var signature = BuildSignature(
                appName,
                GetString(installed, "version"),
                relativePath,
                arguments);
            if (string.Equals(
                    completedSignature,
                    signature,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var startInfo = extension.Equals(
                    ".msi",
                    StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments =
                        $"/i \"{prerequisitePath}\" {arguments}".Trim(),
                    WorkingDirectory = installRoot,
                    UseShellExecute = true,
                }
                : new ProcessStartInfo
                {
                    FileName = prerequisitePath,
                    Arguments = arguments,
                    WorkingDirectory =
                        Path.GetDirectoryName(prerequisitePath) ?? installRoot,
                    UseShellExecute = true,
                };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error =
                    $"Windows could not start {FirstNonEmpty(name, "the required prerequisite")}.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode is not (0 or 1641 or 3010))
            {
                error =
                    $"{FirstNonEmpty(name, "The required prerequisite")} " +
                    $"finished with exit code {process.ExitCode}.";
                return false;
            }

            completedSignature = signature;
            return true;
        }
        catch (Exception exception)
        {
            error =
                $"Windows setup could not be completed: {exception.Message}";
            return false;
        }
    }

    private static bool TryResolveContainedFile(
        string installRoot,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(installRoot) ||
            string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                relativePath));
            if (!candidate.StartsWith(
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSignature(
        string appName,
        string version,
        string relativePath,
        string arguments)
    {
        var value = string.Join(
            '\n',
            appName.Trim().ToLowerInvariant(),
            version.Trim(),
            relativePath.Trim().ToLowerInvariant(),
            arguments.Trim());
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                   ?.Trim() ??
               string.Empty;
    }
}
