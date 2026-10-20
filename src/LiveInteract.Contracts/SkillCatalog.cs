using System.Collections.Generic;
using System.Linq;

namespace LiveInteract.Contracts;

/// <summary>把游戏本身的丝技入口转换为可直接选择的中文效果。</summary>
public sealed class SilkSkill
{
    /// <summary>效果标识。</summary>
    public string Id { get; set; } = "";
    /// <summary>游戏中的简体中文名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>已在游戏资源中核对的全局 FSM 事件。</summary>
    public string Event { get; set; } = "";
}

/// <summary>六种实际丝技，全都使用 silkSpecialFSM，避免要求主播填写事件名。</summary>
public static class SkillCatalog
{
    /// <summary>游戏原生丝技入口。</summary>
    public static IReadOnlyList<SilkSkill> All { get; } = new[]
    {
        new SilkSkill { Id = "cast.silkspear", Name = "丝之矛", Event = "NEEDLE THROW" },
        new SilkSkill { Id = "cast.threadstorm", Name = "灵丝风暴", Event = "THREAD SPHERE" },
        new SilkSkill { Id = "cast.crossstitch", Name = "十字绣", Event = "PARRY" },
        new SilkSkill { Id = "cast.needlestrike", Name = "蓄力斩", Event = "SILK CHARGE" },
        new SilkSkill { Id = "cast.runerage", Name = "符文之怒", Event = "SILK BOMB" },
        new SilkSkill { Id = "cast.palenails", Name = "苍白之爪", Event = "BOSS NEEDLE" }
    };

    /// <summary>根据效果标识找到固定事件。</summary>
    /// <param name="id">效果标识。</param>
    public static SilkSkill? Find(string id) => All.FirstOrDefault(item => item.Id == id);
}
