using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>可见支援为主：补给拾取、分档援军、独立整活，不接管操作、不截断礼物。</summary>
public sealed class DefaultProfileTests
{
    [Fact]
    public void ProfileIsFileBasedVisibleAndDoesNotMutateSaves()
    {
        var profile=Read();Assert.Equal(0,profile.SpawnLimitOverride);Assert.True(profile.Rules.Count>=20);
        Assert.All(profile.Rules,r=>{Assert.Empty(ProfileStore.ValidateRule(r));Assert.Equal(StepKind.Action,r.Root.Kind);Assert.Equal(1,r.Probability);Assert.Equal(0,r.CooldownSeconds);Assert.Equal(0,r.MaximumTriggers);Assert.False(ActionCatalog.All.Single(a=>a.Id==r.Root.ActionId).IsPersistent);});
        Assert.All(profile.Rules.Where(r=>r.Enabled),r=>{Assert.False(r.Root.ActionId.StartsWith("cast.",StringComparison.Ordinal));Assert.DoesNotContain(r.Root.ActionId,new[]{"move.jumpNow","move.dashNow","position.move","scene.warp","time.freeze","time.scale","move.scale","move.speed","move.jump"});});
        Assert.Contains(profile.Rules,r=>!r.Enabled&&r.Root.ActionId=="cast.threadstorm");
    }
    [Theory]
    [InlineData("回血","health.add")][InlineData("回丝","silk.add")]
    [InlineData("小弟","ally.clockwork")][InlineData("怪变大","enemy.growStack")][InlineData("怪变小","enemy.shrinkStack")]
    [InlineData("复制","enemy.cloneSafe")][InlineData("小怪","spawn.bone-goomba")][InlineData("清场","enemy.clearHostiles")][InlineData("恢复","effects.calm")]
    public void ChatCommandSelectsExactlyOneEffect(string text,string action)
    {var m=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="chat",Type=LiveEventType.Chat,Text=text},Read().Rules));Assert.Equal(action,m.Rule.Root.ActionId);Assert.Equal(1,m.Count);}
    [Theory]
    [InlineData("小心心",0,"ally.clockwork", "1")][InlineData("玫瑰",0,"ally.clockwork", "2")]
    [InlineData("棒棒糖",0,"spawn.shellwood-wasp", "1")][InlineData("啤酒",0,"enemy.cloneSafe", "1")]
    [InlineData("其他礼物",10,"ally.clockwork", "2")][InlineData("其他礼物",100,"ally.clockwork", "3")]
    [InlineData("其他礼物",500,"ally.clockwork", "4")][InlineData("单价未知",0,"ally.clockwork", "1")]
    public void GiftNamesAndRealUnitValueSelectOneAction(string gift,long value,string action,string count)
    {var m=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="gift",Type=LiveEventType.Gift,GiftName=gift,GiftUnitValue=value,Count=1},Read().Rules));Assert.Equal(action,m.Rule.Root.ActionId);Assert.Equal(count,m.Rule.Root.Arguments["count"]);if(action.StartsWith("ally."))Assert.Equal("0",m.Rule.Root.Arguments["lifetime"]);}
    [Fact]
    public void GiftQuantityAndLikeRemaindersAreNotDiscarded()
    {
        var rules=Read().Rules;var engine=new RuleEngine(reliable:true);
        Assert.Equal(3000,Assert.Single(engine.Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName="小心心",Count=3000},rules)).Count);
        for(var i=0;i<4;i++)Assert.Empty(engine.Evaluate(new(){Id="c"+i,Type=LiveEventType.Chat,Text="你好"},rules));
        Assert.Equal("health.add",Assert.Single(engine.Evaluate(new(){Id="c4",Type=LiveEventType.Chat,Text="你好"},rules)).Rule.Root.ActionId);
        Assert.Empty(engine.Evaluate(new(){Id="l1",Type=LiveEventType.Like,Count=39},rules));
        Assert.Equal("silk.add",Assert.Single(engine.Evaluate(new(){Id="l2",Type=LiveEventType.Like,Count=11},rules)).Rule.Root.ActionId);
        foreach(var rule in rules.Where(r=>r.Root.ActionId is "enemy.growStack" or "enemy.shrinkStack")){Assert.Equal(EffectStacking.Stack,rule.Root.Stacking);Assert.Equal("0.2",rule.Root.Arguments["value"]);}
    }
    [Theory][InlineData("不要复制")][InlineData("怪别变大")][InlineData("别刷怪")]
    public void OrdinaryChatCannotAccidentallyTriggerDisruption(string text)=>Assert.Empty(new RuleEngine(reliable:true).Evaluate(new(){Id="normal",Type=LiveEventType.Chat,Text=text},Read().Rules));
    private static RuleProfile Read(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null&&!File.Exists(Path.Combine(d.FullName,"LiveInteract.sln")))d=d.Parent;return ProfileStore.Read(File.ReadAllText(Path.Combine(d!.FullName,"profiles","丝之歌-弹幕助力.json")),false);}
}
