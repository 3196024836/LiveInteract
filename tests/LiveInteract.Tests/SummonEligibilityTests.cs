using System.Reflection;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>本轮严格验收白名单；不以攻击能力或旧随机资格代替批准。</summary>
[Collection("Monster pools")]
public sealed class SummonEligibilityTests
{
    private static bool Eligible(MonsterDefinition m)
    {
        var property=typeof(MonsterDefinition).GetProperty("SummonEligible"); Assert.NotNull(property);
        return (bool)property.GetValue(m)!;
    }
    private static string Reason(MonsterDefinition m)
    {
        var property=typeof(MonsterDefinition).GetProperty("SummonExclusionReason"); Assert.NotNull(property);
        return (string)property.GetValue(m)!;
    }
    [Fact] public void ArchiveRemainsCompleteAndOnlyFrozenCandidatesAreOpen()
    {
        var ids=new[]{"spawn.bone-goomba","spawn.bone-spitter","spawn.dock-flyer","spawn.shellwood-wasp","spawn.shellwood-gnat","spawn.slab-fly-large"};
        Assert.Equal(234,MonsterCatalog.All.Count);
        Assert.Equal(ids.OrderBy(id=>id),MonsterCatalog.All.Where(Eligible).Select(m=>m.Id).OrderBy(id=>id));
        Assert.All(MonsterCatalog.All,m=>{if(Eligible(m)){Assert.True(m.CanFight);Assert.Empty(Reason(m));}else Assert.NotEmpty(Reason(m));});
        foreach(var id in ids)
        {
            Assert.Contains(ActionCatalog.UserFacing,a=>a.Id==id);
            Assert.Same(MonsterCatalog.Find(id),SummonCommands.Resolve(id));
            Assert.Empty(ActionValidation.Validate(new(){ActionId=id}));
        }
        Assert.True(MonsterCatalog.Find("spawn.bone-worm-blueturret")!.CanFight);
        Assert.True(MonsterCatalog.Find("spawn.seth")!.CanFight);
        Assert.Contains(ActionCatalog.UserFacing,a=>a.Id=="ally.clockwork");
        Assert.Empty(ActionValidation.Validate(new(){ActionId="ally.clockwork"}));
    }
    [Fact] public void CatalogDescriptionsDistinguishFrozenCandidatesFromPendingSceneArchive()
    {
        var descriptors=(IReadOnlyList<ActionDescriptor>)typeof(ActionCatalog).GetMethod("Load",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null)!;
        foreach(var monster in MonsterCatalog.All.Where(m=>m.SummonEligible))
        {
            var description=descriptors.Single(a=>a.Id==monster.Id).Description;
            Assert.Contains("本轮召唤白名单单位，验收记录随对应版本提供。",description);
            Assert.DoesNotContain("不进入默认礼物随机池",description);
        }
        foreach(var monster in MonsterCatalog.All.Where(m=>!m.SummonEligible&&m.SourceScene.Length>0))
            Assert.Contains("尚未列入召唤白名单",descriptors.Single(a=>a.Id==monster.Id).Description);
    }
    [Theory][InlineData("weak")][InlineData("strongBoss")][InlineData("ordinary")][InlineData("all")]
    public void EveryEmptyPoolFailsExplicitlyWithoutFallback(string pool)
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        var error=Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("no-approved",pool));
        Assert.Contains("批准",error.Message);Assert.Contains(pool,error.Message);
        Assert.Contains(ActionValidation.Validate(new(){ActionId="summon.random",Arguments=new(){{"pool",pool}}}),e=>e.Contains("批准"));
    }
    [Fact] public void LegacyRandomHelpersCannotBypassEmptyApprovalList()
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("legacy",false));
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("legacy",true));
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.Boss("legacy"));
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.FreeAlly("legacy"));
    }
    [Theory][InlineData("spawn.swamp-barnacle","固定地形")][InlineData("spawn.lilypad-trap","固定地形")][InlineData("spawn.centipede-trap","固定地形")]
    [InlineData("spawn.bloom-puncher","固定地形")][InlineData("spawn.bloom-shooter","固定地形")]
    [InlineData("spawn.bone-worm-blueturret","固定地形")][InlineData("spawn.seth","Dormant")]
    public void ExclusionsRejectNamedDirectLegacyAndConversionCalls(string id,string cause)
    {
        var monster=MonsterCatalog.Find(id)!;Assert.Contains(cause,Reason(monster));
        Assert.Contains(cause,Assert.Throws<ArgumentException>(()=>SummonCommands.Resolve(monster.Name)).Message);
        Assert.Contains(cause,Assert.Throws<ArgumentException>(()=>NativeSummons.Convert(monster,1)).Message);
        Assert.Contains(ActionValidation.Validate(new(){ActionId=id}),e=>e.Contains(cause));
        foreach(var name in monster.Aliases.Concat(new[]{id,monster.Name,monster.ObjectName,monster.Asset}).Where(n=>n.Length>0).Distinct())
        {
            foreach(var action in new[]{"summon.named","summon.named.ally","summon.named.enemy","enemy.spawn","enemy.asset","boss.asset"})
            {
                var key=action.StartsWith("summon.")?"name":action=="enemy.spawn"?"enemy":"asset";
                Assert.Contains(ActionValidation.Validate(new(){ActionId=action,Arguments=new(){{key,name}}}),e=>e.Contains(cause));
            }
        }
        Assert.Contains(ActionValidation.Validate(new(){ActionId="ally.convert."+id}),e=>e.Contains(cause));
    }
    [Theory]
    [InlineData("spawn.swamp-shaman", "Attack Choice", true)]
    [InlineData("spawn.bone-hunter-chief", "Attack Choice", true)]
    [InlineData("spawn.shakra", "Attack Choice", true)]
    [InlineData("spawn.arborium-keeper", "Attack Choice", true)]
    [InlineData("spawn.lost-lace", "Attack Choice", true)]
    [InlineData("spawn.pilgrim-bell-thrower", "身体", true)]
    [InlineData("spawn.lilypad-fly", "原作", false)]
    [InlineData("spawn.lifeblood-fly", "原作", false)]
    public void StaticExclusionsAreExplicitAndDoNotRewriteAttackCapability(string id, string cause, bool canFight)
    {
        var monster = MonsterCatalog.Find(id)!;
        Assert.False(monster.SummonEligible);
        Assert.Contains(cause, monster.SummonExclusionReason);
        Assert.Equal(canFight, monster.CanFight);
        Assert.DoesNotContain(ActionCatalog.UserFacing, a => a.Id == id);
        Assert.Throws<ArgumentException>(() => SummonCommands.Resolve(id));
        Assert.Throws<ArgumentException>(() => NativeSummons.Convert(monster, 1));
        Assert.NotEmpty(ActionValidation.Validate(new() { ActionId = "ally.convert." + id }));
    }
    [Fact] public void ExistingRuleCanBeReadWithoutOverwritingItsActionOrParameters()
    {
        var source=new RuleProfile{Rules=new(){new(){Name="旧赛斯",Root=new(){ActionId="spawn.seth",Arguments=new(){{"count","3"},{"power","4"}}}}}};
        var read=ProfileStore.Read(JsonConvert.SerializeObject(source),false);
        Assert.Equal("spawn.seth",read.Rules[0].Root.ActionId);Assert.Equal("4",read.Rules[0].Root.Arguments["power"]);
        Assert.Contains(ProfileStore.ValidateRule(read.Rules[0]),e=>e.Contains("Dormant"));
        Assert.Contains("Dormant",RuleDisplay.Effect(read.Rules[0].Root));
    }
    [Fact] public void UnknownLegacyAssetDoesNotFallbackToAnUnsafeSceneObject()
    {
        Assert.Contains(ActionValidation.Validate(new(){ActionId="enemy.asset",Arguments=new(){{"asset","Unknown/Unsafe.prefab"}}}),e=>e.Contains("批准"));
    }

    [Fact] public void LegacyNativeAliasesCannotBypassPendingApproval()
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        foreach(var ally in NativeSummons.Allies)
            Assert.Contains(ActionValidation.Validate(new(){ActionId=ally.Id}),e=>e.Contains("不可召唤"));
    }
    [Theory][InlineData("spawn.lilypad-fly")][InlineData("spawn.lifeblood-fly")]
    public void ArchivedUnavailableRulesRemainReadableButCannotExecute(string id)
    {
        var profile=new RuleProfile{Rules=new(){new(){Name="旧归档规则",Root=new(){ActionId=id}}}};
        var read=ProfileStore.Read(JsonConvert.SerializeObject(profile),false);
        Assert.Equal(id,read.Rules[0].Root.ActionId);
        Assert.Contains(ProfileStore.ValidateRule(read.Rules[0]),e=>e.Contains("没有攻击"));
    }
    [Fact] public void SerializedArchiveCannotInjectApprovalAndQaCopiesDoNotChangeStaticCatalog()
    {
        var original=MonsterCatalog.Find("spawn.gloomfly")!;
        var copy=JsonConvert.DeserializeObject<MonsterDefinition>(JsonConvert.SerializeObject(original))!;
        JsonConvert.PopulateObject("{\"Tier\":\"Weak\",\"SummonEligible\":true,\"SummonExclusionReason\":\"\"}",copy);
        Assert.False(copy.SummonEligible);
        copy.SummonEligible=true;SummonEligibility.Require(copy);
        Assert.False(original.SummonEligible);
        Assert.Throws<ArgumentException>(()=>SummonEligibility.Require(original));
    }
    [Fact] public void StrongPoolDoesNotRequireAnyApprovedBoss()
    {
        using var approved = new ApprovedSummonsScope(new[] { "spawn.slab-fly-large" });
        Assert.Equal("spawn.slab-fly-large", NativeSummons.RandomMonster("strong-only", "strongBoss").Id);
        Assert.Throws<InvalidOperationException>(() => NativeSummons.Boss("no-boss"));
    }

    [Fact] public void ApprovedSamplesAloneUseNewAndLegacySemanticsAndRetryStably()
    {
        var ids=new[]{"spawn.bone-goomba","spawn.slab-fly-large","spawn.coral-hunter"};
        using var approved=new ApprovedSummonsScope(ids);
        Assert.Equal(3,MonsterCatalog.All.Count(m=>m.SummonEligible));
        Assert.Same(MonsterCatalog.Find(ids[0]),SummonCommands.Resolve("颅潜虫"));
        Assert.NotNull(NativeSummons.Convert(MonsterCatalog.Find(ids[0])!,1));
        Assert.Empty(ActionValidation.Validate(new(){ActionId=ids[0]}));
        Assert.Empty(ActionValidation.Validate(new(){ActionId="ally.native.bone-goomba"}));
        Assert.Empty(ActionValidation.Validate(new(){ActionId="enemy.spawn",Arguments=new(){{"enemy","颅潜虫"}}}));
        foreach(var pool in new[]{"weak","strongBoss","ordinary","all"})
        {
            for(var i=0;i<100;i++)
            {
                var seed="approved-"+i;var m=NativeSummons.RandomMonster(seed,pool);
                Assert.Contains(m.Id,ids);Assert.True(m.SummonEligible&&m.CanFight);
                if(pool is "ordinary" or "all")Assert.True(m.RandomEligible);
                Assert.Equal(m.Id,NativeSummons.RandomMonster(seed,pool).Id);
                if(pool=="weak")Assert.Equal(MonsterTier.Weak,m.Tier);
                if(pool=="strongBoss")Assert.NotEqual(MonsterTier.Weak,m.Tier);
                if(pool=="ordinary")Assert.False(m.IsBoss);
                Assert.Equal(NativeSummons.RandomMonster(seed,false).Id,NativeSummons.RandomMonster(seed,"ordinary").Id);
                Assert.Equal(NativeSummons.RandomMonster(seed,true).Id,NativeSummons.RandomMonster(seed,"all").Id);
            }
        }
        Assert.Equal(ids[2],NativeSummons.Boss("approved-boss").Id);
        var weak=MonsterCatalog.Find(ids[0])!;var random=weak.RandomEligible;
        try
        {
            using var onlyWeak=new ApprovedSummonsScope(new[]{ids[0]});
            weak.RandomEligible=false;
            Assert.Equal(weak.Id,NativeSummons.RandomMonster("new-approved","weak").Id);
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("legacy-restricted","ordinary"));
        }
        finally{weak.RandomEligible=random;}
    }

    [Theory]
    [InlineData("spawn.bone-spitter")]
    [InlineData("spawn.dock-flyer")]
    public void ApprovedSceneWeakUnitsEnterNewPoolWithoutExpandingLegacyPools(string id)
    {
        using var approved=new ApprovedSummonsScope(new[]{id});
        var monster=MonsterCatalog.Find(id)!;
        Assert.False(monster.RandomEligible);
        for(var i=0;i<40;i++)
        {
            var seed="scene-retry-"+i;
            Assert.Equal(id,NativeSummons.RandomMonster(seed,"weak").Id);
            Assert.Equal(id,NativeSummons.RandomMonster(seed,"weak").Id);
        }
        Assert.Empty(ActionValidation.Validate(new(){ActionId="summon.random.ally",Arguments=new(){{"pool","weak"}}}));
        foreach(var pool in new[]{"ordinary","all","strongBoss"})
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("no-legacy-expansion",pool));
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("bool-ordinary",false));
        Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("bool-all",true));
        Assert.False(monster.RandomEligible);
    }

    [Theory]
    [InlineData("spawn.bone-goomba","weak")]
    [InlineData("spawn.slab-fly-large","strongBoss")]
    [InlineData("spawn.coral-hunter","strongBoss")]
    public void NewTierPoolsIgnoreLegacyOnlyFlagButLegacyHelpersKeepIt(string id,string pool)
    {
        using var approved=new ApprovedSummonsScope(new[]{id});
        var monster=MonsterCatalog.Find(id)!;var original=monster.RandomEligible;
        try
        {
            monster.RandomEligible=false;
            Assert.Equal(id,NativeSummons.RandomMonster("new-tier",pool).Id);
            Assert.Empty(ActionValidation.Validate(new(){ActionId="summon.random",Arguments=new(){{"pool",pool}}}));
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("legacy",false));
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("legacy",true));
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.Boss("legacy-boss"));
            Assert.Throws<InvalidOperationException>(()=>NativeSummons.FreeAlly("legacy-free"));
        }
        finally{monster.RandomEligible=original;}
    }

    [Theory]
    [InlineData("spawn.bone-spitter")]
    [InlineData("spawn.dock-flyer")]
    public void LegacyFlagIsNotNeededByNewPoolButApprovalStillIs(string id)
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        Assert.False(MonsterCatalog.Find(id)!.RandomEligible);
        foreach(var pool in new[]{"weak","strongBoss"})
        {
            Assert.Contains("批准",Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster("unapproved",pool)).Message);
            Assert.Contains(ActionValidation.Validate(new(){ActionId="summon.random",Arguments=new(){{"pool",pool}}}),error=>error.Contains("批准"));
        }
    }

    /// <summary>仅敌方开放的单位只进敌方召唤与敌方随机池，友方入口仍拒绝。</summary>
    [Fact] public void EnemyOnlyUnitsJoinOnlyEnemyPools()
    {
        using var approved=new ApprovedSummonsScope(new[]{"spawn.bone-goomba"});
        var enemyOnly=MonsterCatalog.Find("spawn.bone-spitter")!;
        try
        {
            enemyOnly.EnemyOnlyEligible=true;
            Assert.Empty(SummonEligibility.RejectionReason(enemyOnly,false));
            Assert.Contains("友军",SummonEligibility.RejectionReason(enemyOnly,true));
            var enemyPicks=Enumerable.Range(0,64).Select(i=>NativeSummons.RandomMonster("e"+i,"weak",false).Id).ToHashSet();
            var allyPicks=Enumerable.Range(0,64).Select(i=>NativeSummons.RandomMonster("a"+i,"weak",true).Id).ToHashSet();
            Assert.Contains("spawn.bone-spitter",enemyPicks);
            Assert.DoesNotContain("spawn.bone-spitter",allyPicks);
            Assert.Empty(ActionValidation.Validate(new(){ActionId="summon.named.enemy",Arguments=new(){["name"]="spawn.bone-spitter"}}));
            Assert.NotEmpty(ActionValidation.Validate(new(){ActionId="summon.named.ally",Arguments=new(){["name"]="spawn.bone-spitter"}}));
        }
        finally { enemyOnly.EnemyOnlyEligible=false; }
    }
}

/// <summary>仅单元测试构造批准样本，finally/Dispose还原；不写审核清单，不用于QA或生产。</summary>
internal sealed class ApprovedSummonsScope:IDisposable
{
    private readonly (MonsterDefinition Monster,bool Eligible)[] original=MonsterCatalog.All.Select(m=>(m,m.SummonEligible)).ToArray();
    public ApprovedSummonsScope(IEnumerable<string>? ids=null)
    {
        var approved=(ids??MonsterCatalog.All.Where(m=>m.CanFight&&m.Id is not ("spawn.bone-worm-blueturret" or "spawn.seth")).Select(m=>m.Id)).ToHashSet(StringComparer.Ordinal);
        foreach(var monster in MonsterCatalog.All)monster.SummonEligible=approved.Contains(monster.Id);
    }
    public void Dispose(){foreach(var entry in original)entry.Monster.SummonEligible=entry.Eligible;}
}
