using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamLoader.App.Hosting;

/// <summary>
/// The frontend code Tools for Steam runs inside Steam. Like Decky Loader, the
/// injector only sends a tiny loader through the debugger; the loader pulls
/// the bundle from the local API server (<c>/frontend/&lt;id&gt;</c>), where the
/// browser caches it by content hash and stack traces point at a real URL.
/// </summary>
public sealed class FrontendBundleRegistry
{
    public const string RoutePrefix = "/frontend/";

    private readonly Dictionary<string, FrontendBundle> _bundles = new(StringComparer.OrdinalIgnoreCase);

    public FrontendBundleRegistry(Uri apiBaseUri, string apiSessionToken)
    {
        ApiBaseUri = apiBaseUri;
        ApiSessionToken = apiSessionToken;
    }

    public Uri ApiBaseUri { get; }

    public string ApiSessionToken { get; }

    /// <summary>
    /// Registers a classic script bundle. The body keeps today's contract: it is
    /// evaluated in the page's global scope (sloppy mode, like a script tag) and
    /// its completion value is "injected" once it attached.
    /// </summary>
    public FrontendBundle AddScript(string id, string versionProperty, string template)
    {
        var version = ComputeVersion($"{template}\n{ApiSessionToken}");
        var body = string.Join(
            "\n",
            ApplyPlaceholders(template, version),
            $"window[{JsonSerializer.Serialize(versionProperty)}] = {JsonSerializer.Serialize(version)};",
            "\"injected\";");
        var bundle = new FrontendBundle(id, $"{id}.js", versionProperty, version, body, IsModule: false);
        _bundles[bundle.FileName] = bundle;
        return bundle;
    }

    /// <summary>Registers an ES module (strict mode, loaded with <c>import()</c>).</summary>
    public FrontendBundle AddModule(string id, string versionProperty, string source)
    {
        var version = ComputeVersion($"{source}\n{ApiSessionToken}");
        var bundle = new FrontendBundle(
            id,
            $"{id}.mjs",
            versionProperty,
            version,
            ApplyPlaceholders(source, version),
            IsModule: true);
        _bundles[bundle.FileName] = bundle;
        return bundle;
    }

    public bool TryGet(string fileName, out FrontendBundle bundle)
    {
        return _bundles.TryGetValue(fileName, out bundle!);
    }

    public IReadOnlyCollection<FrontendBundle> All => _bundles.Values;

    /// <summary>Path below the API base for a bundle, including its cache-busting version.</summary>
    public static string GetRelativeUrl(FrontendBundle bundle)
    {
        return $"{RoutePrefix.TrimStart('/')}{bundle.FileName}?v={bundle.Version}";
    }

    /// <summary>
    /// Parses <c>/frontend/&lt;file&gt;</c>. Only flat file names are accepted.
    /// </summary>
    public static bool TryParseRequestPath(string? path, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = path[RoutePrefix.Length..];
        if (candidate.Length is 0 or > 64 ||
            !candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_') ||
            candidate.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    private string ApplyPlaceholders(string template, string version)
    {
        return template
            .Replace("__STEAMLOADER_API_BASE__", ApiBaseUri.ToString(), StringComparison.Ordinal)
            .Replace("__STEAMLOADER_SCRIPT_VERSION__", version, StringComparison.Ordinal);
    }

    internal static string ComputeVersion(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes[..8]);
    }
}

public sealed record FrontendBundle(
    string Id,
    string FileName,
    string VersionProperty,
    string Version,
    string Body,
    bool IsModule);
