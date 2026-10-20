using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>按真实单价分档，不把连送数量或名称当作单价；旧配置不要求新字段。</summary>
public sealed class GiftTierTests
{
    [Theory]
    [InlineData(0, "base")][InlineData(9, "base")][InlineData(10, "scout")][InlineData(99, "scout")][InlineData(100, "guard")][InlineData(499, "guard")][InlineData(500, "elite")]
    public void UnitValueSelectsOneTier(long value, string expected)
    {
        RuleDefinition Rule(string id,long min,long max)=>new() { Id=id,Name=id,Enabled=true,StopAfterMatch=true,Trigger=new(){Type=LiveEventType.Gift,MinGiftUnitValue=min,MaxGiftUnitValue=max} };
        var rules=new[]{Rule("elite",500,0),Rule("guard",100,499),Rule("scout",10,99),Rule("base",0,9)};
        var match=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="gift",Type=LiveEventType.Gift,GiftUnitValue=value,Count=30},rules));
        Assert.Equal(expected,match.Rule.Id);Assert.Equal(30,match.Count);
    }
}
