using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
public sealed class CompanionFollowPolicyTests
{
    [Theory]
    [InlineData(2,false,false)][InlineData(3,false,false)][InlineData(3.6,false,true)]
    [InlineData(3,true,true)][InlineData(2.4,true,false)]
    public void FollowHasStopStartHysteresis(double distance,bool moving,bool expected)=>Assert.Equal(expected,CompanionFollowPolicy.ShouldFollow(distance,moving));
    [Theory]
    [InlineData(true,20,false)][InlineData(false,.4,false)][InlineData(false,.8,true)]
    public void RecallRequiresOffscreenGrace(bool visible,double seconds,bool expected)=>Assert.Equal(expected,CompanionFollowPolicy.CanRecall(visible,seconds));
    [Theory]
    [InlineData(true,false,true,false)][InlineData(false,true,true,false)][InlineData(false,false,false,false)][InlineData(false,false,true,true)]
    public void NeverSteerAnAttackOrNativeFriendlyAi(bool enemy,bool native,bool safe,bool expected)=>Assert.Equal(expected,CompanionFollowPolicy.CanSteer(enemy,native,safe));
    [Theory]
    [InlineData(-.02,true,true,true)] [InlineData(-.04,true,true,false)]
    [InlineData(.15,true,true,true)] [InlineData(.4,true,true,true)]
    [InlineData(.6,true,true,false)] [InlineData(.15,false,true,false)]
    [InlineData(.15,true,false,false)] [InlineData(-.1,true,true,false)]
    public void SmallStepsNeedGroundAndHeadroom(double rise,bool grounded,bool clear,bool expected) =>
        Assert.Equal(expected,CompanionFollowPolicy.CanWalkUpStep(rise,grounded,clear));
    [Fact] public void TargetsAreStickyUntilDeadOrOutsideLeash()
    {
        Assert.True(CompanionFollowPolicy.KeepTarget(true,false,false,12,15));
        Assert.False(CompanionFollowPolicy.KeepTarget(false,false,false,2,2));
        Assert.False(CompanionFollowPolicy.KeepTarget(true,true,false,2,2));
        Assert.False(CompanionFollowPolicy.KeepTarget(true,false,true,2,2));
        Assert.False(CompanionFollowPolicy.KeepTarget(true,false,false,19,2));
        Assert.False(CompanionFollowPolicy.KeepTarget(true,false,false,2,23));
    }
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData(null, false, true, true)]
    [InlineData(null, true, true, false)]
    [InlineData(null, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(null, null, true, false)]
    [InlineData(null, null, false, true)]
    public void NativeCrawlerFacingSurvivesItsStoppedIdle(bool? walkFacesLeft, bool? floorCrawlerFacesRight, bool fallbackFacesRight, bool expected)
        => Assert.Equal(expected, CompanionFollowPolicy.SpriteFacesLeft(walkFacesLeft, floorCrawlerFacesRight, fallbackFacesRight));

    /// <summary>赛斯局部坐标约束也属于来源竞技场，根召唤物不能被钳回旧场地；普通单位和子部件不改。</summary>
    /// <param name="ownedRoot">是否直接位于根召唤物。</param>
    /// <param name="localSpace">原作是否使用局部坐标。</param>
    /// <param name="expected">是否保留原约束执行。</param>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void RootArenaConstraintsCannotClampSummonsEvenInLocalSpace(bool ownedRoot, bool localSpace, bool expected)
        => Assert.Equal(expected, CompanionFollowPolicy.CanApplySourcePositionConstraint(ownedRoot, localSpace));
}
