namespace SteamLoader.App.Infrastructure.Assets;

internal static class EmbeddedAssetReader
{
    public static string ReadText(string relativePath)
    {
        using var reader = new StreamReader(OpenStream(relativePath));
        return reader.ReadToEnd();
    }

    public static byte[] ReadBytes(string relativePath)
    {
        using var stream = OpenStream(relativePath);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Stream OpenStream(string relativePath)
    {
        var assembly = typeof(EmbeddedAssetReader).Assembly;
        var resourceSuffix = relativePath.Replace('\\', '.').Replace('/', '.');
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

        var stream = resourceName is not null
            ? assembly.GetManifestResourceStream(resourceName)
            : null;

        if (stream is null)
        {
            throw new FileNotFoundException($"Embedded asset not found: {relativePath}", resourceSuffix);
        }

        return stream;
    }
}
