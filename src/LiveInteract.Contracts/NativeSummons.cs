using System;
using System.Collections.Generic;
using System.Linq;
namespace LiveInteract.Contracts;
/// <summary>每项直接对应原生预制体，不替换模型、动画或装到机械随从上。</summary>
public sealed class NativeAllyDefinition
{
    public string Id { get; set; } = "";
    public string MonsterId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Flying { get; set; }
    public double DamageMultiplier { get; set; } = 1;
}
public static class NativeSummons
{
    public static IReadOnlyList<NativeAllyDefinition> Allies { get; } = new[] {
        new NativeAllyDefinition { Id="ally.native.bone-goomba",MonsterId="spawn.bone-goomba",Name="友方颅潜虫" },
        new NativeAllyDefinition { Id="ally.native.understore-thrower",MonsterId="spawn.understore-thrower",Name="友方圣厂投掷工" },
        new NativeAllyDefinition { Id="ally.native.shellwood-gnat",MonsterId="spawn.shellwood-gnat",Name="友方壳木蚋虫",Flying=true },
        new NativeAllyDefinition { Id="ally.native.shellwood-wasp",MonsterId="spawn.shellwood-wasp",Name="强力友方木蜂",Flying=true,DamageMultiplier=4 },
        new NativeAllyDefinition { Id="ally.native.slab-fly-large",MonsterId="spawn.slab-fly-large",Name="强力友方狱卒蝇",Flying=true,DamageMultiplier=8 }
    };
    public static NativeAllyDefinition? Find(string id)=>Allies.FirstOrDefault(a=>a.Id==id);
    // 稳定的执行单位种子：加载重试不改抽签结果，不使用进程随机化的string.GetHashCode。
    public static int Choice(string executionId,int count)
    { if(count<1)throw new ArgumentOutOfRangeException(nameof(count));var seed=17;foreach(var ch in executionId ?? "")seed=unchecked(seed*31+ch);return new Random(seed).Next(count); }
    public static MonsterDefinition Boss(string executionId)
    {
        var pool=MonsterCatalog.All.Where(m=>m.IsBoss&&m.CanFight&&m.SummonEligible&&m.RandomEligible).OrderBy(m=>m.Id,StringComparer.Ordinal).ToArray();
        if(pool.Length==0) { throw new InvalidOperationException("Boss池没有本轮批准且具有随机资格的战斗单位。"); }
        return pool[Choice(executionId,pool.Length)];
    }
    public static NativeAllyDefinition FreeAlly(string executionId)
    {
        var pool=Allies.Take(2).Where(a=>MonsterCatalog.Find(a.MonsterId) is { CanFight:true, SummonEligible:true, RandomEligible:true }).ToArray();
        if(pool.Length==0) { throw new InvalidOperationException("旧免费友军池没有本轮批准且具有随机资格的战斗单位。"); }
        return pool[Choice(executionId,pool.Length)];
    }
    /// <summary>旧调用保持含义：false 为非 Boss 战斗单位，true 为全部战斗单位。</summary>
    public static MonsterDefinition RandomMonster(string executionId,bool includeBoss)=>RandomMonster(executionId,includeBoss?"all":"ordinary");

    /// <summary>新分档池按可攻击、召唤白名单与 Tier 抽取；旧 ordinary/all 另保留 RandomEligible。重试使用同一执行单位 ID。</summary>
    public static MonsterDefinition RandomMonster(string executionId,string pool)
    {
        Func<MonsterDefinition,bool> accepts = pool switch
        {
            "weak" => m=>m.Tier==MonsterTier.Weak,
            "strongBoss" => m=>m.Tier is MonsterTier.Strong or MonsterTier.Boss,
            "ordinary" => m=>m.RandomEligible&&!m.IsBoss,
            "all" => m=>m.RandomEligible,
            _ => throw new ArgumentException("未知怪物随机池："+pool,nameof(pool))
        };
        var candidates=MonsterCatalog.All.Where(m=>m.CanFight&&m.SummonEligible&&accepts(m)).OrderBy(m=>m.Id,StringComparer.Ordinal).ToArray();
        if(candidates.Length==0) { throw new InvalidOperationException("该怪物随机池没有符合资格的本轮批准战斗单位："+pool); }
        return candidates[Choice(executionId,candidates.Length)];
    }
    public static NativeAllyDefinition Convert(MonsterDefinition m,double power)
    {
        SummonEligibility.Require(m);
        return new NativeAllyDefinition { Id="ally.convert."+m.Id,MonsterId=m.Id,Name="友方"+m.Name,Flying=!m.IsBoss && new[]{"fly","wasp","gnat","gloom","crow","wraith"}.Any(n=>m.ObjectName.IndexOf(n,StringComparison.OrdinalIgnoreCase)>=0),DamageMultiplier=power };
    }
}
