using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LiveInteract.Contracts;

/// <summary>共享动作描述，供界面生成编辑器和游戏端验证使用。</summary>
public static class ActionCatalog
{
    /// <summary>代码已实现的动作定义；实际游戏验证结果由会话诊断独立报告。</summary>
    public static IReadOnlyList<ActionDescriptor> All { get; } = Load();

    /// <summary>主播直接选择的效果；旧版技术入口仅为配置兼容保留。</summary>
    public static IEnumerable<ActionDescriptor> UserFacing => All.Where(item => item.Id != "enemy.asset" && item.Id != "boss.asset" && item.Id != "skill.cast");

    /// <summary>读取程序集内的固定动作定义。</summary>
    /// <returns>完整动作目录。</returns>
    private static IReadOnlyList<ActionDescriptor> Load()
    {
        using var stream = typeof(ActionCatalog).Assembly.GetManifestResourceStream("LiveInteract.Contracts.actions.json"); // 内置定义。
        using var reader = new StreamReader(stream ?? throw new InvalidDataException("缺少内置动作目录。")); // UTF-8 读取器。
        var actions = JsonConvert.DeserializeObject<List<ActionDescriptor>>(reader.ReadToEnd()) ?? throw new InvalidDataException("动作目录为空。");
        foreach (var monster in MonsterCatalog.All)
        {
            actions.Add(new ActionDescriptor
            {
                Id = monster.Id, Name = "生成" + monster.Name + (monster.IsBoss ? "（Boss）" : ""),
                Category = monster.IsBoss ? "生成Boss" : "生成怪物",
                Description = "使用本机游戏的" + monster.Name + "预制体。" +
                    (monster.IsBoss ? "跨场景 Boss 属试验功能，AI 可能依赖原场景，请用独立存档测试。" : "可在角色附近生成，紧急停止会清理本工具生成的对象。"),
                Parameters = new List<ActionParameter>
                {
                    new ActionParameter { Key = "count", Label = "每次生成数量（只）", Kind = ParameterKind.Integer, DefaultValue = "1", Minimum = 1, Maximum = 20 },
                    new ActionParameter { Key = "offsetX", Label = "水平距离（右侧为正）", Kind = ParameterKind.Number, DefaultValue = "3", Minimum = -30, Maximum = 30 },
                    new ActionParameter { Key = "offsetY", Label = "垂直距离（上方为正）", Kind = ParameterKind.Number, DefaultValue = "1", Minimum = -20, Maximum = 20 }
                }
            });
        }
        foreach (var skill in SkillCatalog.All)
        {
            actions.Add(new ActionDescriptor { Id = skill.Id, Name = "施放" + skill.Name, Category = "丝技", Description = "调用游戏原生" + skill.Name + "入口；等待角色当前动作结束；灵丝不足时补足本次施放所需，不永久解锁技能。" });
        }
        return actions;
    }
}
