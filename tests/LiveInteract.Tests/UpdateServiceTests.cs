using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>更新判断与载荷边界：远端更新才提示，版本号不可比较时不打扰用户。</summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.7.7", "0.7.6", true)]
    [InlineData("0.8.0", "0.7.9", true)]
    [InlineData("0.7.6", "0.7.6", false)]
    [InlineData("0.7.5", "0.7.6", false)]
    public void OnlyNewerRemoteVersionsCount(string remote, string local, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewer(remote, local));

    [Theory][InlineData("")][InlineData("abc")][InlineData("0.7")]
    public void UncomparableVersionsNeverUpdate(string remote)
        => Assert.False(UpdateService.IsNewer(remote, "0.7.6"));

    [Fact] public void ManifestDefaultsToProjectReleases()
    {
        Assert.StartsWith("https://raw.githubusercontent.com/", UpdateService.DefaultManifest, StringComparison.Ordinal);
        Assert.EndsWith("update/latest.json", UpdateService.DefaultManifest, StringComparison.Ordinal);
    }

    [Fact] public void BundledBrowserAndDatabasesArePreserved()
    {
        Assert.True(UpdateService.IsPreserved("WebView2Runtime/msedgewebview2.exe"));
        Assert.True(UpdateService.IsPreserved("data/user.db"));
        Assert.False(UpdateService.IsPreserved("payload/mod/LiveInteract.Silksong.dll"));
        Assert.False(UpdateService.IsPreserved("直播互动工具箱.exe"));
    }
}
