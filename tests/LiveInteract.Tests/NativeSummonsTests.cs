using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
public sealed class NativeSummonsTests
{
    [Fact] public void FourKindsUseFourOriginalPrefabs()
    {Assert.Equal(4,NativeSummons.Allies.Count);Assert.Equal(4,NativeSummons.Allies.Select(a=>a.MonsterId).Distinct().Count());Assert.All(NativeSummons.Allies,a=>Assert.NotNull(MonsterCatalog.Find(a.MonsterId)));}
    [Fact] public void RandomBossIsOneStableMappedBossPerExecutionUnit()
    {foreach(var id in Enumerable.Range(0,100).Select(i=>"unit-"+i)){var chosen=NativeSummons.Boss(id);Assert.True(chosen.IsBoss);Assert.Equal(chosen.Id,NativeSummons.Boss(id).Id);}}
    [Fact] public void FreeRandomOnlyUsesTheTwoBasicKinds()
    {var free=NativeSummons.Allies.Take(2).Select(a=>a.Id).ToArray();foreach(var id in Enumerable.Range(0,100).Select(i=>"free-"+i))Assert.Contains(NativeSummons.FreeAlly(id).Id,free);}
    [Fact] public void BossQuantityCannotBeIncreasedByArguments()
    {Assert.NotEmpty(ActionValidation.Validate(new(){ActionId="boss.random",Arguments=new(){["count"]="2"}}));}
}
