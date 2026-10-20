using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>默认配置的当前边界：17 条规则、四件具名礼物、100 赞一档，远程关键词优先于近战关键词。</summary>
public sealed class DefaultProfileTests
{
    private static RuleProfile Read(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null&&!File.Exists(Path.Combine(d.FullName,"LiveInteract.sln")))d=d.Parent;return ProfileStore.Read(File.ReadAllText(Path.Combine(d!.FullName,"profiles","丝之歌-弹幕助力.json")),false);}
    private static RuleMatch[] Evaluate(LiveEvent input)=>new RuleEngine(reliable:true).Evaluate(input,Read().Rules).ToArray();

    [Fact] public void EveryRuleHasOneValidatedEffect()
    {var p=Read();Assert.Equal(0,p.SpawnLimitOverride);Assert.Equal(17,p.Rules.Count);Assert.All(p.Rules,r=>{Assert.True(r.Enabled);Assert.Empty(ProfileStore.ValidateRule(r));Assert.Equal(StepKind.Action,r.Root.Kind);Assert.Equal(1,r.Probability);Assert.Equal(0,r.CooldownSeconds);Assert.False(ActionCatalog.All.Single(a=>a.Id==r.Root.ActionId).IsPersistent);});}

    [Theory]
    [InlineData("小弟","ally.native.bone-goomba")][InlineData("远程小弟","ally.native.understore-thrower")]
    [InlineData("小怪","spawn.bone-goomba")][InlineData("远程小怪","spawn.understore-thrower")]
    [InlineData("回血","health.add")][InlineData("回蓝","silk.add")][InlineData("回丝","silk.add")]
    [InlineData("怪变大","enemy.growStack")][InlineData("怪变小","enemy.shrinkStack")]
    [InlineData("金币","currency.rain")][InlineData("大招","cast.threadstorm")][InlineData("灵丝风暴","cast.threadstorm")]
    public void ChatCommandsMapToApprovedEffects(string text,string action)
    {var m=Assert.Single(Evaluate(new(){Id="c",Type=LiveEventType.Chat,Text=text}));Assert.Equal(action,m.Rule.Root.ActionId);}

    [Fact] public void LongKeywordsWinOverTheirSubstring()
    {
        // 「远程小弟」包含「小弟」；规则顺序必须让更长的关键词先命中，否则远程小弟永远打不出来。
        Assert.Equal("ally.native.understore-thrower",Assert.Single(Evaluate(new(){Id="a",Type=LiveEventType.Chat,Text="远程小弟"})).Rule.Root.ActionId);
        Assert.Equal("spawn.understore-thrower",Assert.Single(Evaluate(new(){Id="b",Type=LiveEventType.Chat,Text="远程小怪"})).Rule.Root.ActionId);
    }

    [Fact] public void FreeRecoveryIsOneAndPermanentCompanionsHaveNoTimer()
    {foreach(var r in Read().Rules.Where(r=>r.Root.ActionId is "health.add" or "silk.add"))Assert.Equal("1",r.Root.Arguments["value"]);foreach(var r in Read().Rules.Where(r=>r.Root.Arguments.ContainsKey("lifetime")&&r.Root.ActionId.StartsWith("ally.native.")))Assert.Equal("0",r.Root.Arguments["lifetime"]);}

    [Theory]
    [InlineData("小心心","summon.random.ally","2","1")]
    [InlineData("玫瑰","summon.random.enemy","1","1")]
    [InlineData("棒棒糖","summon.random.ally","2","4")]
    [InlineData("给到夯","summon.random.enemy","1","2")]
    public void NamedGiftMatchesExactlyOnce(string name,string action,string count,string power)
    {var m=Assert.Single(Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName=name,GiftUnitValue=1}));Assert.Equal(action,m.Rule.Root.ActionId);Assert.Equal(count,m.Rule.Root.Arguments["count"]);Assert.Equal(power,m.Rule.Root.Arguments["power"]);}

    [Fact] public void NamedGiftDoesNotDependOnPriceField()
    {var m=Assert.Single(Evaluate(new(){Id="g0",Type=LiveEventType.Gift,GiftName="给到夯",GiftUnitValue=0}));Assert.Equal("summon.random.enemy",m.Rule.Root.ActionId);}

    [Fact] public void UnknownGiftFallsThroughToNoRule()
    {Assert.Empty(Evaluate(new(){Id="gx",Type=LiveEventType.Gift,GiftName="这个礼物不存在",GiftUnitValue=99}));}

    [Theory][InlineData("复制")][InlineData("刷怪")][InlineData("清场")]
    public void RemovedFreeHostileCommandsDoNotMatch(string text)=>Assert.Empty(Evaluate(new(){Id="c",Type=LiveEventType.Chat,Text=text}));

    [Fact] public void LikeFiresEveryHundredAndKeepsRemainder()
    {
        var engine=new RuleEngine(reliable:true);var rules=Read().Rules;
        Assert.Empty(engine.Evaluate(new(){Id="l1",Type=LiveEventType.Like,Count=99},rules));
        var first=Assert.Single(engine.Evaluate(new(){Id="l2",Type=LiveEventType.Like,Count=1},rules));
        Assert.Equal("currency.rain",first.Rule.Root.ActionId);
        Assert.Empty(engine.Evaluate(new(){Id="l3",Type=LiveEventType.Like,Count=99},rules));
        var second=Assert.Single(engine.Evaluate(new(){Id="l4",Type=LiveEventType.Like,Count=51},rules));
        Assert.Equal("currency.rain",second.Rule.Root.ActionId);
    }

    [Fact] public void GiftQuantityIsRetainedPerEvent()
    {var m=Assert.Single(Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName="小心心",GiftUnitValue=1,Count=3000}));Assert.Equal(3000,m.Count);}
}
