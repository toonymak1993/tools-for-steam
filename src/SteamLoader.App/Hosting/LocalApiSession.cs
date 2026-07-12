using System.Security.Cryptography;

namespace SteamLoader.App.Hosting;

public static class LocalApiSession
{
    public const string HeaderName = "X-TFS-Session";
    public const string QueryName = "tfsSession";

    private static readonly string[] TrustedOriginHosts =
    [
        "steamloopback.host",
        "steamcommunity.com",
        "store.steampowered.com"
    ];

    private static readonly object FileLock = new();

    public static string GetOrCreateDefault()
    {
        return GetOrCreate(Path.Combine(AppContext.BaseDirectory, "data", "local-api-session.token"));
    }

    public static string GetOrCreate(string path)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (IsValidToken(existing))
                {
                    return existing;
                }
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            File.WriteAllText(path, token);

            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch
            {
                // The token still protects browser access when the filesystem does not support Hidden.
            }

            return token;
        }
    }

    public static bool IsTrustedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            // Some Steam CEF surfaces use an opaque origin. The session token remains mandatory.
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var trustedScheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("steamloopback.host", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
        return trustedScheme &&
            TrustedOriginHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsPublicResourceRequest(string method, string? path)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/plugin-store/images/built-in/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/plugin-store/images/catalog/", StringComparison.OrdinalIgnoreCase) ||
            IsCommunityPluginFilePath(path);
    }

    public static bool IsAuthorized(string expectedToken, string? headerToken, string? queryToken, string method)
    {
        var suppliedToken = !string.IsNullOrWhiteSpace(headerToken)
            ? headerToken
            : method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? queryToken : null;

        if (string.IsNullOrWhiteSpace(expectedToken) || string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedToken);
        var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(suppliedToken);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static bool IsValidToken(string token)
    {
        return token.Length >= 32 && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsCommunityPluginFilePath(string path)
    {
        const string prefix = "/api/plugin-store/community/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            path.Contains("/files/", StringComparison.OrdinalIgnoreCase);
    }
}
