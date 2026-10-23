using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
[Collection("Monster pools")]
public sealed class NativeSummonsTests:IDisposable
{
    private readonly ApprovedSummonsScope approved=new();
    public void Dispose()=>approved.Dispose();
    [Theory]
    [InlineData(false, false, 10, false, false)]
    [InlineData(false, false, 0, false, true)]
    [InlineData(false, false, -1, false, true)]
    [InlineData(false, false, 0, true, false)]
    [InlineData(false, false, -1, true, false)]
    [InlineData(false, true, 0, true, true)]
    [InlineData(true, false, 0, true, true)]
    public void NativeSpecialDeathKeepsTheRootForItsOwnPhaseHandler(bool missing, bool dead, int hp, bool specialDeath, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldRemoveNativeRoot(missing, dead, hp, specialDeath));

    [Fact] public void EveryAllyKindUsesItsOwnOriginalPrefab()
    {Assert.Equal(5,NativeSummons.Allies.Count);Assert.Equal(5,NativeSummons.Allies.Select(a=>a.MonsterId).Distinct().Count());Assert.All(NativeSummons.Allies,a=>Assert.NotNull(MonsterCatalog.Find(a.MonsterId)));}
    [Fact] public void RandomBossIsOneStableMappedBossPerExecutionUnit()
    {foreach(var id in Enumerable.Range(0,100).Select(i=>"unit-"+i)){var chosen=NativeSummons.Boss(id);Assert.True(chosen.IsBoss);Assert.Equal(chosen.Id,NativeSummons.Boss(id).Id);}}
    [Fact] public void FreeRandomOnlyUsesTheTwoBasicKinds()
    {var free=NativeSummons.Allies.Take(2).Select(a=>a.Id).ToArray();foreach(var id in Enumerable.Range(0,100).Select(i=>"free-"+i))Assert.Contains(NativeSummons.FreeAlly(id).Id,free);}
    [Fact] public void BossQuantityCannotBeIncreasedByArguments()
    {Assert.NotEmpty(ActionValidation.Validate(new(){ActionId="boss.random",Arguments=new(){["count"]="2"}}));}
    [Fact] public void RandomPoolsRespectBossBoundary()
    {
        // 普通池必须排除 Boss，强力池必须包含 Boss；同一执行单位抽签稳定。
        foreach(var id in Enumerable.Range(0,200).Select(i=>"pool-"+i))
        {
            var ordinary=NativeSummons.RandomMonster(id,false);
            Assert.False(ordinary.IsBoss);
            Assert.Equal(ordinary.Id,NativeSummons.RandomMonster(id,false).Id);
            var strong=NativeSummons.RandomMonster(id,true);
            Assert.Equal(strong.Id,NativeSummons.RandomMonster(id,true).Id);
        }
        var strongPool=Enumerable.Range(0,400).Select(i=>"s-"+i).Select(id=>NativeSummons.RandomMonster(id,true)).ToArray();
        Assert.Contains(strongPool,m=>m.IsBoss);
    }

    [Fact] public void EveryMonsterHasOneFactionSelector()
    {
        foreach(var monster in MonsterCatalog.All.Where(m=>m.CanFight&&m.SummonEligible))
        {
            var descriptor=Assert.Single(ActionCatalog.UserFacing,a=>a.Id==monster.Id);
            var faction=Assert.Single(descriptor.Parameters,p=>p.Key=="faction");
            Assert.Equal(ParameterKind.Choice,faction.Kind);
            Assert.Equal(new[]{"ally","enemy"},faction.Choices.Keys.OrderBy(k=>k));
            Assert.Equal("enemy",faction.DefaultValue);
        }
        Assert.DoesNotContain(ActionCatalog.UserFacing,a=>a.Id.StartsWith("ally.native.")||a.Id is "summon.random.ally" or "summon.random.enemy" or "summon.named.ally" or "summon.named.enemy");
    }
    [Theory]
    [InlineData("spawn.bone-goomba",false)]
    [InlineData("summon.random.enemy",false)]
    [InlineData("summon.random.ally",true)]
    [InlineData("ally.native.understore-thrower",true)]
    public void LegacyFactionRemainsUnchanged(string id,bool expected)
    {
        Assert.Equal(expected,SummonCommands.IsFriendly(id,new()));
        Assert.True(SummonCommands.IsFriendly(id,new(){{"faction","ally"}}));
        Assert.False(SummonCommands.IsFriendly(id,new(){{"faction","enemy"}}));
    }
    [Fact] public void LegacyEditorMigrationPreservesNumbersAndLifetime()
    {
        var old=new ActionStep{ActionId="ally.native.understore-thrower",Arguments=new(){{"count","3"},{"value","2"},{"lifetime","5"}}};
        SummonCommands.Canonicalize(old);
        Assert.Equal("spawn.understore-thrower",old.ActionId);
        Assert.Equal("ally",old.Arguments["faction"]);Assert.Equal("3",old.Arguments["count"]);
        Assert.Equal("2",old.Arguments["power"]);Assert.Equal("5",old.Arguments["lifetime"]);
        Assert.DoesNotContain("value",old.Arguments.Keys);
        Assert.Empty(ActionValidation.Validate(new(){ActionId=old.ActionId,Arguments=old.Arguments}));
    }
    [Theory]
    [InlineData("ally")][InlineData("enemy")]
    public void UnifiedSummonRoundTrips(string faction)
    {
        var request=new ActionRequest{ActionId="summon.random",Arguments=new(){{"count","2"},{"pool","all"},{"power","4"},{"faction",faction}}};
        Assert.Empty(ActionValidation.Validate(request));
        Assert.NotEmpty(ActionValidation.Validate(new(){ActionId="spawn.bone-goomba",Arguments=new(){{"faction","neutral"}}}));
    }

    [Fact] public void UnverifiedSceneUnitsDoNotEnterGiftRandomPools()
    {
        Assert.True(MonsterCatalog.All.Count>200);
        Assert.All(MonsterCatalog.All.Where(m=>m.SourceScene.Length>0&&m.Id!="spawn.dustroach-caged"),m=>Assert.False(m.RandomEligible));
        foreach(var index in Enumerable.Range(0,100))Assert.True(NativeSummons.RandomMonster("isolated-"+index,true).RandomEligible);
        Assert.True(MonsterCatalog.Find("spawn.coral-hunter")!.IsBoss);
    }
    [Fact] public void OriginalCallsDoNotAcquireNewOptionalParameters()
    {
        var original=new ActionRequest{ActionId="spawn.bone-goomba",Arguments=new(){{"count","2"}}};
        ActionValidation.FillDefaults(original);
        Assert.DoesNotContain("faction",original.Arguments.Keys);Assert.DoesNotContain("lifetime",original.Arguments.Keys);Assert.DoesNotContain("power",original.Arguments.Keys);
    }
    [Theory]
    [InlineData("enemy.spawn")]
    [InlineData("enemy.random")]
    [InlineData("enemy.clone")]
    [InlineData("enemy.cloneSafe")]
    [InlineData("boss.clone")]
    public void SceneSummonsExposeFactionWithoutChangingOldCalls(string id)
    {
        var descriptor=ActionCatalog.All.Single(a=>a.Id==id);
        Assert.Single(descriptor.Parameters,p=>p.Key=="faction");
        Assert.Empty(ActionValidation.ValidateStored(new(){ActionId=id,Arguments=new(){{"faction","ally"}}}));
        var old=new ActionRequest{ActionId=id};ActionValidation.FillDefaults(old);
        Assert.False(old.Arguments.ContainsKey("faction"));
    }
    /// <summary>清场是独立维护指令，不借用重置属性或仅清敌人的旧入口。</summary>
    [Fact]
    public void ClearAllSummonsIsSafeMaintenanceAction()
    {
        var action=Assert.Single(ActionCatalog.All,a=>a.Id=="game.clearSummons");
        Assert.False(action.RequiresGameplay);
        Assert.False(action.IsPersistent);
        Assert.False(action.IsTemporary);
        Assert.Empty(action.Parameters);
        Assert.Empty(ActionValidation.Validate(new(){ActionId="game.clearSummons"}));
        Assert.Contains("友",action.Description);
        Assert.Contains("敌",action.Description);
    }
    /// <summary>成年熔岩蛞蝓使用同种原作地面副本，不能把倒挂的天花板模板当普通随行小怪。</summary>
    [Fact]
    public void AdultTarSlugUsesItsOriginalGroundVariantForBothFactions()
    {
        var monster=Assert.IsType<MonsterDefinition>(MonsterCatalog.Find("spawn.tar-slug-huge"));
        Assert.Equal("成年熔岩蛞蝓",monster.Name);
        Assert.Equal("dock_11",monster.SourceScene);
        Assert.Equal("scenes_scenes_scenes/dock_11.bundle",monster.Bundle);
        Assert.Equal("Tar Slug Huge (1)",monster.ObjectName);
        Assert.Equal("Tar Slug Huge (1)",monster.Asset);
        Assert.Equal(MonsterTier.Strong,monster.Tier);
        Assert.True(monster.CanFight);
    }

}
