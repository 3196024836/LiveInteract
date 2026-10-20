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
    { var pool=MonsterCatalog.All.Where(m=>m.IsBoss).OrderBy(m=>m.Id,StringComparer.Ordinal).ToArray();return pool[Choice(executionId,pool.Length)]; }
    public static NativeAllyDefinition FreeAlly(string executionId)=>Allies[Choice(executionId,2)];
    public static MonsterDefinition RandomMonster(string executionId,bool includeBoss)=>ChooseMonster(executionId,includeBoss);
    private static MonsterDefinition ChooseMonster(string id,bool bosses) { var pool=MonsterCatalog.All.Where(m=>bosses||!m.IsBoss).OrderBy(m=>m.Id,StringComparer.Ordinal).ToArray();return pool[Choice(id,pool.Length)]; }
    public static NativeAllyDefinition Convert(MonsterDefinition m,double power) => new NativeAllyDefinition { Id="ally.convert."+m.Id,MonsterId=m.Id,Name="友方"+m.Name,Flying=!m.IsBoss && new[]{"fly","wasp","gnat","gloom","crow","wraith"}.Any(n=>m.ObjectName.IndexOf(n,StringComparison.OrdinalIgnoreCase)>=0),DamageMultiplier=power };
}
