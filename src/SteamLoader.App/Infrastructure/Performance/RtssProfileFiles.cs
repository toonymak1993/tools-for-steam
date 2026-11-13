namespace SteamLoader.App.Infrastructure.Performance;

/// <summary>Reads RTSS game profile files (Profiles\&lt;game&gt;.exe.cfg) without loading RTSS.</summary>
internal static class RtssProfileFiles
{
    /// <summary>Exe names of game profiles whose [Framerate] Limit is above 0.</summary>
    public static IReadOnlyList<string> FindProfilesWithFrameLimit(string profilesDirectory)
    {
        if (!Directory.Exists(profilesDirectory))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var path in Directory.EnumerateFiles(profilesDirectory, "*.exe.cfg"))
        {
            try
            {
                if (ReadFrameLimit(File.ReadLines(path)) > 0)
                {
                    names.Add(Path.GetFileName(path)[..^".cfg".Length]);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return names;
    }

    internal static int ReadFrameLimit(IEnumerable<string> lines)
    {
        var inFramerate = false;
        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inFramerate = line.Equals("[Framerate]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inFramerate &&
                line.StartsWith("Limit=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Limit=".Length..], out var limit))
            {
                return limit;
            }
        }

        return 0;
    }
}
