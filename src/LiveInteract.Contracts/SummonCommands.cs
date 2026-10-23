using System;
using System.Linq;
using System.Collections.Generic;
namespace LiveInteract.Contracts;
/// <summary>准确名称召唤，只解析已声明前缀，不猜测近似怪名。</summary>
public static class SummonCommands
{
    public static string Extract(string text,string prefix)
    {
        if(text==null||!text.StartsWith(prefix,StringComparison.Ordinal))throw new ArgumentException("请输入"+prefix+"＋准确怪物名");
        var name=text.Substring(prefix.Length).TrimStart(' ','\t','+','＋',':','：').Trim();
        if(name.Length==0)throw new ArgumentException("缺少怪物名称，例如："+prefix+" 木蜂");
        return name;
    }
    public static MonsterDefinition Resolve(string name)
    {
        var monster=FindSource(name) ?? throw new ArgumentException("没有匹配到准确名称「"+name+"」。请在游戏功能库查看已批准怪物名；本次没有生成替代单位。");
        SummonEligibility.Require(monster);
        return monster;
    }

    /// <summary>仅映射明确的目录标识、原生资源路径或准确名称；不猜测近似名或场景实例。</summary>
    public static MonsterDefinition? FindSource(string source)=>MonsterCatalog.All.FirstOrDefault(m=>
        m.Aliases.Concat(new[]{m.Id,m.Name,m.ObjectName,m.Asset}).Any(n=>n.Length>0&&string.Equals(n,source,StringComparison.OrdinalIgnoreCase)));

    /// <summary>只复制稳定池内的准确原生种类；清除实例后缀，不用相似名称替代。</summary>
    public static MonsterDefinition? CopyableEncounter(string source)
    {
        var name=(source??"").Trim();
        foreach(var suffix in new[]{" [LiveInteract]","(Clone)"})
            if(name.EndsWith(suffix,StringComparison.Ordinal))name=name.Substring(0,name.Length-suffix.Length).TrimEnd();
        var monster=FindSource(name);
        return monster is {CanFight:true,SummonEligible:true}?monster:null;
    }

    /// <summary>显式阵营优先；未填写的新参数不改变旧配置的友敌含义。</summary>
    public static bool IsFriendly(string actionId,Dictionary<string,string> arguments)
    {
        if(arguments.TryGetValue("faction",out var faction))return faction=="ally";
        return actionId.StartsWith("ally.",StringComparison.Ordinal)||actionId.EndsWith(".ally",StringComparison.Ordinal);
    }
    public static bool IsSummon(string id)=>MonsterCatalog.Find(id)!=null||id.StartsWith("summon.",StringComparison.Ordinal)||id.StartsWith("ally.",StringComparison.Ordinal)||id is "boss.random" or "boss.clone" or "enemy.cloneSafe" or "enemy.clone" or "enemy.spawn" or "enemy.random" or "enemy.asset" or "boss.asset";
    public static ActionParameter Faction(string value)=>new(){Key="faction",Label="召唤阵营",Kind=ParameterKind.Choice,DefaultValue=value,Choices=new(){{"ally","友方 · 不锁定、不伤害主播"},{"enemy","敌方 · 正常攻击主播"}}};
    /// <summary>仅编辑副本转换为统一入口；数量、强度和持续时间原样保留。</summary>
    public static void Canonicalize(ActionStep step)
    {
        var friendly=IsFriendly(step.ActionId,step.Arguments);
        if(NativeSummons.Find(step.ActionId) is {} ally)
        {
            step.ActionId=ally.MonsterId;
            step.Arguments["power"]=step.Arguments.TryGetValue("value",out var power)?power:ally.DamageMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
            step.Arguments.Remove("value");
        }
        else if(step.ActionId is "summon.random.ally" or "summon.random.enemy")step.ActionId="summon.random";
        else if(step.ActionId is "summon.named.ally" or "summon.named.enemy")step.ActionId="summon.named";
        else return;
        step.Arguments["faction"]=friendly?"ally":"enemy";
    }
}
