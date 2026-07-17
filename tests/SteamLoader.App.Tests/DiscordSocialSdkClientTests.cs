using SteamLoader.App.Infrastructure.Discord;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class DiscordSocialSdkClientTests
{
    [Fact]
    public async Task WindowsRuntimeLoadsAndReportsExpectedVersion()
    {
        await using var client = new DiscordSocialSdkClient();

        Assert.Equal("1.9.17380", DiscordSocialSdkClient.GetRuntimeVersion());
    }
}
