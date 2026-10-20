using LiveInteract.Contracts;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>直播提示只在触发后出现，逐条渐隐，触发者不能被规则名取代。</summary>
public sealed class OverlayNoticeTests
{
    [Fact]
    public void EmptyGameplayHasNoPermanentStatusPanel()
    {
        var queue = new FadingNoticeQueue(); Assert.Empty(queue.Read(0));
        queue.Add(0, "观众甲", "生成木蜂 ×3", true, 5);
        queue.Add(1, "观众乙", "伤害 ×2", true, 5);
        var rows = queue.Read(2); Assert.Equal(2, rows.Count); Assert.Equal("观众甲", rows[0].Triggerer); Assert.Equal("观众乙", rows[1].Triggerer);
        Assert.Contains("×3", rows[0].Effect); Assert.True(queue.Read(4.8)[0].Alpha < 1);
        Assert.Empty(queue.Read(7));
    }

    [Fact]
    public void ParameterPresentationIncludesCountMultiplierAndFullNickname()
    {
        Assert.Contains("×3", ActionPresentation.Describe(new() { ActionId = "spawn.shellwood-wasp", Arguments = new() { ["count"] = "3" } }));
        Assert.Contains("×2", ActionPresentation.Describe(new() { ActionId = "damage.all", Arguments = new() { ["value"] = "2" } }));
        Assert.Equal("观众甲", DisplayNames.Clean("观众\n甲"));
        Assert.Contains("2点", ActionPresentation.Describe(new() { ActionId = "health.blue", Arguments = new() { ["value"] = "2" } }));
    }
}
