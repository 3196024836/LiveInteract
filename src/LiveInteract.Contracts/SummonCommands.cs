using System;
using System.Linq;
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
    public static MonsterDefinition Resolve(string name)=>MonsterCatalog.All.FirstOrDefault(m=>string.Equals(m.Name,name,StringComparison.OrdinalIgnoreCase)||string.Equals(m.ObjectName,name,StringComparison.OrdinalIgnoreCase)||string.Equals(m.Id,name,StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("没有匹配到准确名称「"+name+"」。请在游戏功能库查看原作怪物名；本次没有生成替代单位。");
}
