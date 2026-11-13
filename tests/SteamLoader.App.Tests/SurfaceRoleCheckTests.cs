using SteamLoader.App.Hosting;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class SurfaceRoleCheckTests
{
    [Fact]
    public void RoleCheckAcceptsFreshPagesAndTheSameRoleOnly()
    {
        var expression = QuickAccessShellInjector.BuildRoleCheckExpression("shared");

        Assert.Equal("!window.__tfsSurfaceRole || window.__tfsSurfaceRole === \"shared\"", expression);
    }
}
