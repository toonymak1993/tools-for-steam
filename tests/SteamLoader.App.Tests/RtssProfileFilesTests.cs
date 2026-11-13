using SteamLoader.App.Infrastructure.Performance;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class RtssProfileFilesTests
{
    [Fact]
    public void FindsGameProfilesThatStillCarryALimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tfs-rtss-profiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "re9.exe.cfg"), "[OSD]\nEnableOSD=1\n[Framerate]\nLimit=144\nLimitDenominator=1\n");
            File.WriteAllText(Path.Combine(root, "game.exe.cfg"), "[Framerate]\nLimit=0\n");
            File.WriteAllText(Path.Combine(root, "Global"), "[Framerate]\nLimit=60\n");
            File.WriteAllText(Path.Combine(root, "other.exe.cfg"), "[OSD]\nLimit=90\n");

            Assert.Equal(["re9.exe"], RtssProfileFiles.FindProfilesWithFrameLimit(root));
            Assert.Empty(RtssProfileFiles.FindProfilesWithFrameLimit(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
