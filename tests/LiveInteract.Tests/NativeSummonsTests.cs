using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
public sealed class NativeSummonsTests
{
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
}
