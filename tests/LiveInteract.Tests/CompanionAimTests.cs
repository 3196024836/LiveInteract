using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>校验原作重力和垂直起速不变时，抛射在下降途中穿过目标落点。</summary>
public sealed class CompanionAimTests
{
    [Theory]
    [InlineData("spawn.pilgrim-bell-thrower", true)]
    [InlineData("spawn.pilgrim-moss-spitter", true)]
    [InlineData("spawn.bone-spitter", false)]
    [InlineData("spawn.other", false)]
    [InlineData(null, false)]
    public void OnlyVerifiedArcingWeaponsUseTargetBodyAim(string? asset, bool expected)
        => Assert.Equal(expected, CompanionAim.UsesTargetedArc(asset));

    [Theory]
    [InlineData(14.9,-.5,19,-60)]
    [InlineData(-14.9,-.5,19,-60)]
    [InlineData(12,0,22,-60)]
    [InlineData(-9,1,22,-60)]
    [InlineData(4,-2,22,-60)]
    public void BallisticShotCrossesTargetOnDescent(double dx,double dy,double vy,double gravity)
    {
        var vx=CompanionAim.HorizontalSpeed(dx,dy,vy,gravity);
        Assert.NotNull(vx);
        var time=dx/vx.Value;
        Assert.True(time>0);
        Assert.True(vy+gravity*time<0);
        Assert.Equal(dy,vy*time+.5*gravity*time*time,6);
    }
    [Theory]
    [InlineData(22,0,0)]
    [InlineData(22,-60,10)]
    public void ImpossibleOrNonBallisticShotRetainsOriginal(double vy,double gravity,double dy)
        =>Assert.Null(CompanionAim.HorizontalSpeed(6,dy,vy,gravity));
}
