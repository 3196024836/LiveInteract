using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LiveInteract.Contracts;

/// <summary>由游戏本地资源核对的具名敌人映射，不包含游戏资源内容。</summary>
public sealed class MonsterDefinition
{
    /// <summary>跨控制台和 Mod 的稳定效果标识。</summary>
    public string Id { get; set; } = "";
    /// <summary>游戏简体中文名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>原生预制体根名称。</summary>
    public string ObjectName { get; set; } = "";
    /// <summary>游戏自身的非场景资源包。</summary>
    public string Bundle { get; set; } = "";
    /// <summary>该资源包里的精确资源路径。</summary>
    public string Asset { get; set; } = "";
    /// <summary>是否需要额外战斗场景适配的 Boss。</summary>
    public bool IsBoss { get; set; }
    /// <summary>从原始预制体实际引用核对的动画包，避免跨区域时丢失动画库。</summary>
    public List<string> AnimationBundles { get; set; } = new();
}

/// <summary>离线可选的具体怪物名单；资源只从用户自己的游戏载入。</summary>
public static class MonsterCatalog
{
    /// <summary>已核对资源路径和生命组件的怪物。</summary>
    public static IReadOnlyList<MonsterDefinition> All { get; } = Load();

    /// <summary>根据效果标识查找怪物。</summary>
    /// <param name="id">稳定标识。</param>
    /// <returns>具名映射或空。</returns>
    public static MonsterDefinition? Find(string id) => All.FirstOrDefault(item => item.Id == id);

    /// <summary>读取内置映射。</summary>
    private static IReadOnlyList<MonsterDefinition> Load()
    {
        using var stream = typeof(MonsterCatalog).Assembly.GetManifestResourceStream("LiveInteract.Contracts.monsters.json");
        using var reader = new StreamReader(stream ?? throw new InvalidDataException("缺少怪物名单。"));
        return JsonConvert.DeserializeObject<List<MonsterDefinition>>(reader.ReadToEnd())!;
    }
}
