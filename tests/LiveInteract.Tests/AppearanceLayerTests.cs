using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>叠层不限次数、保留触发者、各自独立到期，不覆盖也不指数放大。</summary>
public sealed class AppearanceLayerTests
{
    [Fact] public void EachTriggerHasOwnAttributionAndExpiry()
    {
        var layers=new AppearanceLayers();layers.Add("甲",true,.2,8,0);layers.Add("乙",true,.2,8,2);
        Assert.Equal(2,layers.Count);Assert.Equal(1.4,layers.Multiplier,8);
        layers.Tick(8.1);Assert.Equal("乙",Assert.Single(layers.Items).Who);Assert.Equal(1.2,layers.Multiplier,8);
        layers.Tick(10.1);Assert.Empty(layers.Items);Assert.Equal(1,layers.Multiplier);
    }
    [Fact] public void FiveThousandPulsesAreNotTruncatedAndShrinkNeverFlipsGeometry()
    {
        var layers=new AppearanceLayers();for(var i=0;i<5000;i++)layers.Add(i.ToString(),true,.2,8,0);
        Assert.Equal(5000,layers.Count);Assert.Equal(1001,layers.Multiplier,7);
        for(var i=0;i<10000;i++)layers.Add("缩小",false,.2,8,0);
        Assert.True(layers.Multiplier>0);Assert.Equal(15000,layers.Count);
    }
}
