using System.Text.Json;
using Microsoft.Win32;

namespace SteamLoader.App.Infrastructure.StoreSync;

internal sealed record EaAppAvailability(
    string ExecutablePath,
    bool ProtocolRegistered)
{
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(ExecutablePath) ||
        ProtocolRegistered;
}

/// <summary>
/// Detects the official EA app and validates Legendary's per-title link2ea
/// handoff without persisting or logging its short-lived exchange token.
/// </summary>
internal static class EaAppIntegration
{
    internal const string OfficialDownloadUrl = "https://www.ea.com/ea-app";

    private static readonly TimeSpan AvailabilityCacheLifetime =
        TimeSpan.FromSeconds(10);
    private static readonly object AvailabilityGate = new();
    private static EaAppAvailability _cachedAvailability = new(string.Empty, false);
    private static DateTimeOffset _availabilityExpiresAtUtc = DateTimeOffset.MinValue;

    public static EaAppAvailability GetAvailability(bool forceRefresh = false)
    {
        lock (AvailabilityGate)
        {
            if (!forceRefresh &&
                DateTimeOffset.UtcNow < _availabilityExpiresAtUtc)
            {
                return _cachedAvailability;
            }

            var protocolCommand = ReadLinkProtocolCommand();
            _cachedAvailability = new EaAppAvailability(
                FindExecutable(protocolCommand),
                !string.IsNullOrWhiteSpace(protocolCommand));
            _availabilityExpiresAtUtc =
                DateTimeOffset.UtcNow.Add(AvailabilityCacheLifetime);
            return _cachedAvailability;
        }
    }

    internal static bool TryParseHandoffUri(
        string? json,
        string appName,
        out Uri handoffUri)
    {
        handoffUri = null!;
        if (string.IsNullOrWhiteSpace(json) ||
            string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("uri", out var uriNode) ||
                uriNode.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(
                    uriNode.GetString(),
                    UriKind.Absolute,
                    out var candidate) ||
                !candidate.Scheme.Equals(
                    "link2ea",
                    StringComparison.OrdinalIgnoreCase) ||
                !candidate.Host.Equals(
                    "launchgame",
                    StringComparison.OrdinalIgnoreCase) ||
                candidate.AbsoluteUri.Length > 8192 ||
                !HasNonEmptyQueryValue(candidate, "AUTH_PASSWORD"))
            {
                return false;
            }

            var targetAppName = Uri.UnescapeDataString(
                candidate.AbsolutePath.Trim('/'));
            if (!targetAppName.Equals(
                    appName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            handoffUri = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string GetExternalAction(
        bool gameInstalled,
        bool eaAppAvailable,
        string? operationStatus)
    {
        if (gameInstalled)
        {
            return string.Empty;
        }

        if (!eaAppAvailable)
        {
            return "install-client";
        }

        return operationStatus?.Trim().Equals(
                   "action-required",
                   StringComparison.OrdinalIgnoreCase) == true
            ? "continue-provider"
            : "link-account";
    }

    internal static string ExtractExecutablePathFromCommand(string? command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(
            command?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        string candidate;
        if (expanded[0] == '"')
        {
            var closingQuote = expanded.IndexOf('"', 1);
            candidate = closingQuote > 1
                ? expanded[1..closingQuote]
                : string.Empty;
        }
        else
        {
            var executableEnd = expanded.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            candidate = executableEnd >= 0
                ? expanded[..(executableEnd + 4)]
                : string.Empty;
        }

        return candidate.Trim().Trim('"');
    }

    private static bool HasNonEmptyQueryValue(Uri uri, string key)
    {
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Split('=', 2))
            .Any(parts =>
                parts.Length == 2 &&
                Uri.UnescapeDataString(parts[0]).Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(
                    Uri.UnescapeDataString(parts[1])));
    }

    private static string FindExecutable(string protocolCommand)
    {
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Electronic Arts\EA Desktop"),
                     (Registry.CurrentUser, @"SOFTWARE\Electronic Arts\EA Desktop"),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var installLocation =
                    key?.GetValue("InstallLocation") as string ??
                    key?.GetValue("Install Dir") as string;
                if (string.IsNullOrWhiteSpace(installLocation))
                {
                    continue;
                }

                var normalizedLocation = installLocation.Trim().Trim('"');
                foreach (var candidate in new[]
                         {
                             Path.Combine(normalizedLocation, "EADesktop.exe"),
                             Path.Combine(
                                 normalizedLocation,
                                 "EA Desktop",
                                 "EADesktop.exe"),
                         })
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(
                             root,
                             "Electronic Arts",
                             "EA Desktop",
                             "EA Desktop",
                             "EADesktop.exe"),
                         Path.Combine(
                             root,
                             "Electronic Arts",
                             "EA Desktop",
                             "EADesktop.exe"),
                     })
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        foreach (var hive in new[]
                 {
                     Registry.LocalMachine,
                     Registry.CurrentUser,
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\EADesktop.exe");
                var candidate = key?.GetValue(null) as string;
                if (IsEaDesktopExecutable(candidate))
                {
                    return Path.GetFullPath(candidate!.Trim().Trim('"'));
                }
            }
            catch
            {
            }
        }

        var protocolExecutable =
            ExtractExecutablePathFromCommand(protocolCommand);
        if (IsEaDesktopExecutable(protocolExecutable))
        {
            return Path.GetFullPath(protocolExecutable);
        }

        return string.Empty;
    }

    private static bool IsEaDesktopExecutable(string? candidate)
    {
        try
        {
            var normalized = candidate?.Trim().Trim('"') ?? string.Empty;
            return Path.GetFileName(normalized).Equals(
                       "EADesktop.exe",
                       StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(normalized);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadLinkProtocolCommand()
    {
        try
        {
            using var command = Registry.ClassesRoot.OpenSubKey(
                @"link2ea\shell\open\command");
            return command?.GetValue(null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
