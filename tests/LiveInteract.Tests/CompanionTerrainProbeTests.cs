using LiveInteract.Contracts;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>墙体探测只改变改编友军的敌人层，保留地形与其他原作射线。</summary>
public sealed class CompanionTerrainProbeTests
{

    /// <summary>核对友军墙体过滤和敌方、目标、地面等未改变的路径。</summary>
    /// <param name="friendly">是否在改编友军调用内。</param>
    /// <param name="eventName">原作命中事件。</param>
    /// <param name="mask">原作掩码。</param>
    /// <param name="expected">预期本次掩码。</param>
    [Theory]
    [InlineData(true, "WALL", 2304, 256)]
    [InlineData(true, "WALL", 1050880, 1048832)]
    [InlineData(false, "WALL", 2304, 2304)]
    [InlineData(true, "TARGET", 2304, 2304)]
    [InlineData(true, "GROUND", 2304, 2304)]
    [InlineData(true, "", 2304, 2304)]
    [InlineData(true, "WALL", 2048, 2048)]
    [InlineData(true, "WALL", 256, 256)]
    public void OnlyFriendlyWallProbesIgnoreEnemyBodies(bool friendly, string eventName, int mask, int expected)
        => Assert.Equal(expected, CompanionTerrainProbe.FilterMask(mask, 8, 11, friendly, eventName));

    /// <summary>层号由原作枚举传入，不把当前游戏层号固化到纯判定。</summary>
    [Fact]
    public void UsesTheActualGameLayerIdsRatherThanHardcodingThem()
        => Assert.Equal(32, CompanionTerrainProbe.FilterMask(160, 5, 7, true, "WALL"));
    [Theory]
    [InlineData("spawn.bone-hunter-chief", "Control", "Charge", "END", true)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Charge Antic", "END", false)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Charge Bonk", "END", false)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Idle", "END", false)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Charge", "ATTACK", false)]
    [InlineData("spawn.bone-hunter-chief", "Aux", "Charge", "END", false)]
    [InlineData("spawn.bone-flyer-giant", "Control", "Charge", "END", false)]
    [InlineData("spawn.skull-king", "Behaviour", "Charge", "END", false)]
    public void OnlyOriginalChiefChargeEndUsesRealBodyWallContact(string asset, string fsm, string state, string hitEvent, bool expected)
        => Assert.Equal(expected, CompanionTerrainProbe.UsesSolidChargeProbe(asset, fsm, state, hitEvent));

    [Theory]
    [InlineData(true, false, -1, .009998, true)]
    [InlineData(true, false, -.8, .05, true)]
    [InlineData(false, false, -1, .01, false)]
    [InlineData(true, true, -1, .01, false)]
    [InlineData(true, false, 0, .01, false)]
    [InlineData(true, false, 1, .01, false)]
    [InlineData(true, false, -.2, .01, false)]
    [InlineData(true, false, -1, .051, false)]
    [InlineData(true, false, -1, -.01, false)]
    public void BodyProbeCannotUseEnemyTriggerGroundSlopeOrDistantObstacle(bool terrain, bool trigger, double normalDotDirection, double distance, bool expected)
        => Assert.Equal(expected, CompanionTerrainProbe.IsSolidWallImpact(terrain, trigger, normalDotDirection, distance));
}
