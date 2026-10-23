using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>默认配置的当前边界：18 条规则、五件具名礼物、100 赞一档，远程关键词优先于近战关键词。</summary>
public sealed class DefaultProfileTests
{
    private static RuleProfile Read(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null&&!File.Exists(Path.Combine(d.FullName,"LiveInteract.sln")))d=d.Parent;return ProfileStore.Read(File.ReadAllText(Path.Combine(d!.FullName,"profiles","丝之歌-弹幕助力.json")),false);}
    private static RuleMatch[] Evaluate(LiveEvent input)=>new RuleEngine(reliable:true).Evaluate(input,Read().Rules).ToArray();

    [Fact] public void EveryRuleHasOneStructurallyValidEffect()
    {var p=Read();Assert.Equal(0,p.SpawnLimitOverride);Assert.Equal(18,p.Rules.Count);Assert.All(p.Rules,r=>{Assert.True(r.Enabled);Assert.Empty(ActionValidation.ValidateStored(new(){ActionId=r.Root.ActionId,Arguments=r.Root.Arguments,DurationSeconds=r.Root.DurationSeconds,Stacking=r.Root.Stacking,AllowPersistent=true}));Assert.Equal(StepKind.Action,r.Root.Kind);Assert.Equal(1,r.Probability);Assert.Equal(0,r.CooldownSeconds);Assert.False(ActionCatalog.All.Single(a=>a.Id==r.Root.ActionId).IsPersistent);});}

    [Theory]
    [InlineData("小弟","ally.clockwork")][InlineData("远程小弟","spawn.dock-flyer")]
    [InlineData("小怪","spawn.bone-goomba")][InlineData("远程小怪","spawn.bone-spitter")]
    [InlineData("回血","health.add")][InlineData("回蓝","silk.add")][InlineData("回丝","silk.add")]
    [InlineData("怪变大","enemy.growStack")][InlineData("怪变小","enemy.shrinkStack")]
    [InlineData("金币","currency.rain")][InlineData("技能","cast.palenails")]
    public void ChatCommandsMapToConfiguredEffects(string text,string action)
    {var m=Assert.Single(Evaluate(new(){Id="c",Type=LiveEventType.Chat,Text=text}));Assert.Equal(action,m.Rule.Root.ActionId);}

    [Fact] public void SkillCommandUsesPaleClawAndOldStormKeywordsAreGone()
    {
        // 技能口令现在是苍白之爪；旧的大招 / 灵丝风暴关键词不再命中。
        Assert.Empty(Evaluate(new(){Id="a",Type=LiveEventType.Chat,Text="大招"}));
        Assert.Empty(Evaluate(new(){Id="b",Type=LiveEventType.Chat,Text="灵丝风暴"}));
        Assert.NotNull(SkillCatalog.Find("cast.palenails"));
        Assert.Equal("苍白之爪",SkillCatalog.Find("cast.palenails")!.Name);
        Assert.Contains(ActionCatalog.All,a=>a.Id=="cast.palenails");
    }

    [Fact] public void LongKeywordsWinOverTheirSubstring()
    {
        // 「远程小弟」包含「小弟」；规则顺序必须让更长的关键词先命中，否则远程小弟永远打不出来。
        Assert.Equal("spawn.dock-flyer",Assert.Single(Evaluate(new(){Id="a",Type=LiveEventType.Chat,Text="远程小弟"})).Rule.Root.ActionId);
        Assert.Equal("spawn.bone-spitter",Assert.Single(Evaluate(new(){Id="b",Type=LiveEventType.Chat,Text="远程小怪"})).Rule.Root.ActionId);
    }

    [Fact] public void FreeRecoveryIsOneAndPermanentCompanionsHaveNoTimer()
    {
        foreach(var r in Read().Rules.Where(r=>r.Root.ActionId is "health.add" or "silk.add"))Assert.Equal("1",r.Root.Arguments["value"]);
        foreach(var keyword in new[]{"小弟","远程小弟"})
        {
            var rule=Assert.Single(Evaluate(new(){Id="duration-"+keyword,Type=LiveEventType.Chat,Text=keyword})).Rule;
            Assert.True(SummonCommands.IsFriendly(rule.Root.ActionId,rule.Root.Arguments));
            Assert.Equal("0",rule.Root.Arguments["lifetime"]);
        }
    }

    [Theory]
    [InlineData("小怪","spawn.bone-goomba","enemy")]
    [InlineData("远程小弟","spawn.dock-flyer","ally")]
    [InlineData("远程小怪","spawn.bone-spitter","enemy")]
    public void ConvertedBasicChatUsesNativeSummonsWithoutStatBuffs(string text,string action,string faction)
    {
        var rule=Assert.Single(Evaluate(new(){Id="basic-"+text,Type=LiveEventType.Chat,Text=text})).Rule;
        Assert.Equal(action,rule.Root.ActionId);Assert.Equal(faction,rule.Root.Arguments["faction"]);
        Assert.Equal("1",rule.Root.Arguments["count"]);Assert.Equal("1",rule.Root.Arguments["power"]);
        Assert.Equal("0",rule.Root.Arguments["lifetime"]);
    }

    [Theory]
    [InlineData("远程小弟", "spawn.dock-flyer", "码头飞工", "燧石飞工")]
    [InlineData("远程小怪", "spawn.bone-spitter", "焦油螨", "焦油螨")]
    [InlineData("小怪", "spawn.bone-goomba", "颅潜虫", "颅潜虫")]
    public void ConfiguredDefaultsUseFrozenCandidatesWithoutClaimingReleaseAcceptance(string keyword, string id, string name, string catalogName)
    {
        var rule = Assert.Single(Evaluate(new() { Id = "candidate-" + keyword, Type = LiveEventType.Chat, Text = keyword })).Rule;
        Assert.Equal(id, rule.Root.ActionId);
        Assert.Contains(name, rule.Name);
        Assert.True(MonsterCatalog.Find(id)!.SummonEligible);
        Assert.Empty(ProfileStore.ValidateRule(rule));
        Assert.Contains(catalogName, RuleDisplay.Effect(rule.Root));
        Assert.DoesNotContain("不可召唤", RuleDisplay.Effect(rule.Root));
    }

    [Fact] public void BasicCompanionUsesOriginalMechanicalFollower()
    {
        var rule=Assert.Single(Evaluate(new(){Id="mechanical",Type=LiveEventType.Chat,Text="小弟"})).Rule;
        Assert.Equal("ally.clockwork",rule.Root.ActionId);
        Assert.Equal("1",rule.Root.Arguments["count"]);
        Assert.Equal("8",rule.Root.Arguments["value"]);
        Assert.False(rule.Root.Arguments.ContainsKey("faction"));
        Assert.Equal("0",rule.Root.Arguments["lifetime"]);
    }

    [Theory]
    [InlineData("小心心","summon.random.ally","2","1")]
    [InlineData("玫瑰","summon.random.enemy","1","1")]
    [InlineData("棒棒糖","summon.random.ally","2","1")]
    [InlineData("给到夯","summon.random.enemy","1","1")]
    public void NamedGiftMatchesExactlyOnce(string name,string action,string count,string power)
    {var m=Assert.Single(Evaluate(new(){Id="g",Type=LiveEventType.Gift,GiftName=name,GiftUnitValue=1}));Assert.Equal(action,m.Rule.Root.ActionId);Assert.Equal(count,m.Rule.Root.Arguments["count"]);Assert.Equal(power,m.Rule.Root.Arguments["power"]);}

    [Theory]
    [InlineData("小心心", "v06-heart", "weak")]
    [InlineData("玫瑰", "v06-note", "weak")]
    [InlineData("棒棒糖", "v06-strong-friend", "strongBoss")]
    [InlineData("给到夯", "v06-strong-enemy", "strongBoss")]
    public void GiftPoolsUseOriginalStrengthWithoutChangingTriggers(string gift, string id, string pool)
    {
        var rule = Assert.Single(Read().Rules, r => r.Trigger.GiftName == gift);
        Assert.Equal(id, rule.Id);
        Assert.Equal(pool, rule.Root.Arguments["pool"]);
        Assert.Equal("1", rule.Root.Arguments["power"]);
        Assert.Equal(1, rule.Trigger.Threshold);
    }

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
