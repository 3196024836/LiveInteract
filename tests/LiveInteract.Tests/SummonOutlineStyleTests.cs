using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>阵营轮廓颜色与屏幕采样不依赖游戏角色脚本。</summary>
public sealed class SummonOutlineStyleTests
{
    [Fact] public void FriendlyAndHostileUseDifferentBodyOutlineColors()
    {
        Assert.Equal("#63F58D",SummonOutlineStyle.ColorHex(true));
        Assert.Equal("#FF5261",SummonOutlineStyle.ColorHex(false));
        Assert.Equal(2,SummonOutlineStyle.DefaultWidth);
    }
    [Fact] public void EightSamplesFollowTheSilhouetteInEveryDirection()
    {
        Assert.Equal(8,SummonOutlineStyle.Directions.Count);
        Assert.All(SummonOutlineStyle.Directions,p=>Assert.InRange(p.X*p.X+p.Y*p.Y,.999f,1.001f));
        Assert.Equal(8,SummonOutlineStyle.Directions.Select(p=>(p.X,p.Y)).Distinct().Count());
        Assert.Contains(SummonOutlineStyle.Directions,p=>p.X==1&&p.Y==0);
        Assert.Contains(SummonOutlineStyle.Directions,p=>p.X==-1&&p.Y==0);
    }
    [Theory]
    [InlineData(-2,1)][InlineData(0,1)][InlineData(2,2)][InlineData(99,5)]
    public void WidthIsBoundedInPixels(double value,double expected)=>Assert.Equal(expected,SummonOutlineStyle.NormalizeWidth(value));
    [Fact] public void InvalidWidthsHaveAUsableDefault()
    {
        Assert.Equal(2,SummonOutlineStyle.NormalizeWidth(double.NaN));
        Assert.Equal(2,SummonOutlineStyle.NormalizeWidth(double.PositiveInfinity));
    }
    [Theory]
    [InlineData("Sprites/Screen", false)]
    [InlineData("Sprites/Default-ColorFlash", true)]
    [InlineData("Sprites/Default", true)]
    [InlineData("Other/NativeBody", true)]
    public void OriginalLightHazeIsNotAnOpaqueBodyOutline(string shader, bool expected)
        => Assert.Equal(expected, SummonOutlineStyle.ShouldOutlineShader(shader));
    [Theory]
    [InlineData(0, true, -1)]
    [InlineData(12, true, 11)]
    [InlineData(-10, true, -11)]
    [InlineData(0, false, 0)]
    [InlineData(12, false, 12)]
    [InlineData(-32768, true, -32768)]
    public void GroupedContourDrawsBehindBodyWithoutChangingTheGroup(int order, bool grouped, int expected)
        => Assert.Equal(expected, SummonOutlineStyle.SortingOrder(order, grouped));
}
