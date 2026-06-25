using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SteamLoader.App.Hosting;
using SteamLoader.App.Models;

namespace SteamLoader.App.Services;

public sealed class SupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly WindowsShellService _shellService;

    public SupportBundleService(WindowsShellService shellService)
    {
        _shellService = shellService;
    }

    public string Export(
        SteamLoaderHostStatus? hostStatus,
        SteamLoaderGeneralSettingsSnapshot settings)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Tools for Steam");
        Directory.CreateDirectory(outputDirectory);

        var bundlePath = Path.Combine(outputDirectory, $"TFS-Support-{timestamp}.zip");
        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"TFS-Support-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            WriteDiagnostics(stagingDirectory, hostStatus, settings);
            CopyDataFiles(stagingDirectory);

            if (File.Exists(bundlePath))
            {
                File.Delete(bundlePath);
            }

            ZipFile.CreateFromDirectory(stagingDirectory, bundlePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return bundlePath;
        }
        finally
        {
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private void WriteDiagnostics(
        string stagingDirectory,
        SteamLoaderHostStatus? hostStatus,
        SteamLoaderGeneralSettingsSnapshot settings)
    {
        var diagnostics = new
        {
            product = SteamLoaderRuntime.ProductName,
            abbreviation = SteamLoaderRuntime.ShortProductName,
            version = settings.ProductVersion,
            createdAtUtc = DateTimeOffset.UtcNow,
            installPath = AppContext.BaseDirectory,
            executablePath = Environment.ProcessPath,
            operatingSystem = Environment.OSVersion.VersionString,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            shellCommand = _shellService.GetShellCommand(),
            hostStatus,
            settings,
            toolsForSteamProcesses = GetToolsForSteamProcesses()
        };

        File.WriteAllText(
            Path.Combine(stagingDirectory, "diagnostics.json"),
            JsonSerializer.Serialize(diagnostics, JsonOptions));
    }

    private static void CopyDataFiles(string stagingDirectory)
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }

        var targetDirectory = Path.Combine(stagingDirectory, "data");
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.WriteAllText(targetPath, RedactJson(File.ReadAllText(file)));
        }
    }

    private static IReadOnlyList<object> GetToolsForSteamProcesses()
    {
        var currentProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ToolsForSteam";
        return Process.GetProcessesByName(currentProcessName)
            .Select(process =>
            {
                try
                {
                    return new
                    {
                        process.Id,
                        process.ProcessName,
                        startedAt = process.StartTime
                    } as object;
                }
                catch
                {
                    return new
                    {
                        process.Id,
                        process.ProcessName,
                        startedAt = (DateTime?)null
                    } as object;
                }
            })
            .ToArray();
    }

    private static string RedactJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            RedactNode(node);
            return node?.ToJsonString(JsonOptions) ?? "{}";
        }
        catch
        {
            return "{}";
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (IsSensitiveProperty(property.Key))
                    {
                        jsonObject[property.Key] = "[redacted]";
                        continue;
                    }

                    RedactNode(property.Value);
                }
                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    RedactNode(item);
                }
                break;
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        return propertyName.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("password", StringComparison.OrdinalIgnoreCase);
    }
}
