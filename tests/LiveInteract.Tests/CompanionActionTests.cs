using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>友军召唤的共享参数契约；真实跟随、攻击和不伤玩家另由隔离游戏验收。</summary>
public sealed class CompanionActionTests
{
    [Fact]
    public void SummoningHasIndependentCountDamageAndLifetimeWithoutSaveMutation()
    {
        var action = Assert.Single(ActionCatalog.All, a => a.Id == "ally.clockwork");
        Assert.False(action.IsPersistent); Assert.True(action.RequiresGameplay);
        Assert.Equal(20, action.Parameters.Single(p => p.Key == "count").Maximum);
        Assert.Equal(600, action.Parameters.Single(p => p.Key == "lifetime").Maximum);
        Assert.Contains(action.Parameters, p => p.Key == "value");
        Assert.Contains(ActionCatalog.UserFacing, a => a.Id == action.Id);
    }
    [Theory]
    [InlineData("count", "21")]
    [InlineData("value", "0")]
    [InlineData("lifetime", "601")]
    public void InvalidCompanionArgumentsAreRejectedBeforeGameMutation(string key, string value)
    {
        var request = new ActionRequest { ActionId = "ally.clockwork", Arguments = new() { [key] = value } };
        Assert.NotEmpty(ActionValidation.Validate(request));
    }
    [Fact]
    public void DirectOriginalSkillsRemainSelectableWithoutBeingSaveUnlocks()
    {
        Assert.Equal(6, SkillCatalog.All.Count);
        Assert.All(SkillCatalog.All, skill => Assert.False(ActionCatalog.All.Single(a => a.Id == skill.Id).IsPersistent));
    }
}
