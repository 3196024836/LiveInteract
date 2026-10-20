using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>默认配置的免费/付费边界；本轮按用户要求仅更新用例，不执行测试。</summary>
public sealed class DefaultProfileTests
{
    private static RuleProfile Read(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null&&!File.Exists(Path.Combine(d.FullName,"LiveInteract.sln")))d=d.Parent;return ProfileStore.Read(File.ReadAllText(Path.Combine(d!.FullName,"profiles","丝之歌-弹幕助力.json")),false);}
    [Fact] public void EveryRuleHasOneValidatedEffect()
    {var p=Read();Assert.Equal(0,p.SpawnLimitOverride);Assert.Equal(16,p.Rules.Count);Assert.All(p.Rules,r=>{Assert.True(r.Enabled);Assert.Empty(ProfileStore.ValidateRule(r));Assert.Equal(StepKind.Action,r.Root.Kind);Assert.Equal(1,r.Probability);Assert.Equal(0,r.CooldownSeconds);Assert.False(ActionCatalog.All.Single(a=>a.Id==r.Root.ActionId).IsPersistent);});}
    [Theory]
    [InlineData("小弟","ally.native.bone-goomba")][InlineData("飞虫","ally.native.shellwood-gnat")]
    [InlineData("回血","health.add")][InlineData("回丝","silk.add")]
    [InlineData("怪变大","enemy.growStack")][InlineData("怪变小","enemy.shrinkStack")]
    [InlineData("大招","cast.threadstorm")]
    public void FreeChatMapsToApprovedEffects(string text,string action)
    {var m=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="c",Type=LiveEventType.Chat,Text=text},Read().Rules));Assert.Equal(action,m.Rule.Root.ActionId);}
    [Fact] public void FreeRecoveryIsOneAndFreeCompanionsHaveNoTimer()
    {foreach(var r in Read().Rules.Where(r=>r.Root.ActionId is "health.add" or "silk.add"))Assert.Equal("1",r.Root.Arguments["value"]);foreach(var r in Read().Rules.Where(r=>r.Root.ActionId.StartsWith("ally.native.")))Assert.Equal("0",r.Root.Arguments["lifetime"]);}
    [Theory][InlineData("小心心",1,"ally.native.shellwood-wasp")][InlineData("啤酒",2,"spawn.bone-goomba")][InlineData("棒棒糖",9,"spawn.shellwood-wasp")][InlineData("热气球",52,"ally.native.slab-fly-large")][InlineData("跑车",1200,"boss.random")]
    public void NamedPaidGiftMatchesExactlyOnce(string name,long price,string action)
    {var m=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName=name,GiftUnitValue=price},Read().Rules));Assert.Equal(action,m.Rule.Root.ActionId);Assert.Equal("1",m.Rule.Root.Arguments["count"]);}
    // 价格仅为构造测试输入，不是平台实时价目。
    [Theory][InlineData("小心心")][InlineData("啤酒")][InlineData("棒棒糖")][InlineData("热气球")][InlineData("跑车")][InlineData("免费礼物")]
    public void ZeroOrUnknownValueNeverSpawnsHostiles(string gift)
    {var m=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="free",Type=LiveEventType.Gift,GiftName=gift,GiftUnitValue=0},Read().Rules));Assert.Equal("ally.native.free-random",m.Rule.Root.ActionId);}
    [Theory][InlineData("复制")][InlineData("刷怪")][InlineData("小怪")][InlineData("清场")]
    public void RemovedFreeHostileCommandsDoNotMatch(string text)=>Assert.Empty(new RuleEngine(reliable:true).Evaluate(new(){Id="c",Type=LiveEventType.Chat,Text=text},Read().Rules));
    [Fact] public void GiftQuantityAndLikeRemaindersAreRetained()
    {var engine=new RuleEngine(reliable:true);var rules=Read().Rules;Assert.Equal(3000,Assert.Single(engine.Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName="小心心",GiftUnitValue=1,Count=3000},rules)).Count);Assert.Empty(engine.Evaluate(new(){Id="l1",Type=LiveEventType.Like,Count=39},rules));var m=Assert.Single(engine.Evaluate(new(){Id="l2",Type=LiveEventType.Like,Count=11},rules));Assert.Equal("silk.add",m.Rule.Root.ActionId);}
}
