using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LiveInteract.Contracts;

/// <summary>原作数值与攻击结构分档；不表示当前 AI 验收结果。</summary>
public enum MonsterTier { Weak, Strong, Boss }

/// <summary>由游戏本地资源核对的具名敌人映射，不包含游戏资源内容。</summary>
public sealed class MonsterDefinition
{
    /// <summary>跨控制台和 Mod 的稳定效果标识。</summary>
    public string Id { get; set; } = "";
    /// <summary>游戏简体中文名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>原生预制体根名称。</summary>
    public string ObjectName { get; set; } = "";
    /// <summary>原作图鉴内部名称，用于和游戏自己的中文名称对应。</summary>
    public string JournalName { get; set; } = "";
    /// <summary>场景内原生对象的来源；空值表示已有独立预制体。</summary>
    public string SourceScene { get; set; } = "";
    /// <summary>旧 ordinary/all、Boss 与 FreeAlly 的历史随机限制；新分档池不使用此标记，仍严格检查召唤白名单。</summary>
    public bool RandomEligible { get; set; } = true;
    /// <summary>原作不用 HealthManager 的特殊单位，仅在本工具副本上接入原生生命组件。</summary>
    public bool AddNativeHealth { get; set; }
    public int NativeHealth { get; set; } = 30;
    public bool CanFight { get; set; } = true;
    /// <summary>当前生产召唤白名单资格；冻结候选可供最终验收，不表示完整发布验收通过。仅从独立清单加载。</summary>
    [JsonIgnore]
    public bool SummonEligible { get; set; }
    /// <summary>仅作为敌方开放（不会跟随主播但能正常作战）；仅从独立清单加载。</summary>
    [JsonIgnore]
    public bool EnemyOnlyEligible { get; set; }
    /// <summary>敌方召唤资格：友方白名单单位，或单独列为仅敌方开放的单位。</summary>
    [JsonIgnore]
    public bool EnemySummonEligible => SummonEligible || EnemyOnlyEligible;
    /// <summary>未批准或明确排除的原因，不把运行时故障记为原作无攻击。</summary>
    [JsonIgnore]
    public string SummonExclusionReason { get; set; } = "尚未通过本轮完整实机验收，暂不开放召唤。";
    public string CombatFsm { get; set; }="";
    public string CombatState { get; set; }="";
    /// <summary>原作以场景机关/禁用弱点承伤的特殊单位，副本按本体原生形状补足受击框。</summary>
    public bool BodyReceiver { get; set; }

    public List<string> Aliases { get; set; } = new();
    /// <summary>游戏自身的非场景资源包。</summary>
    public string Bundle { get; set; } = "";
    /// <summary>该资源包里的精确资源路径。</summary>
    public string Asset { get; set; } = "";
    /// <summary>是否需要额外战斗场景适配的 Boss。</summary>
    public bool IsBoss { get; set; }
    /// <summary>逐项显式分类；Boss 与旧 IsBoss 标记一致。</summary>
    [JsonProperty(Required = Required.Always)]
    [JsonConverter(typeof(StringEnumConverter))]
    public MonsterTier Tier { get; set; }
    /// <summary>分类依据，独立于随机资格及运行时适配结果。</summary>
    public string TierReason { get; set; } = "";
    /// <summary>从原始预制体实际引用核对的动画包，避免跨区域时丢失动画库。</summary>
    public List<string> AnimationBundles { get; set; } = new();
}

/// <summary>离线可选的具体怪物名单；资源只从用户自己的游戏载入。</summary>
public static class MonsterCatalog
{
    private static readonly Dictionary<string,string> cabBundles=LoadCabBundles();
    public static string? BundleForCab(string cab)=>cabBundles.TryGetValue(cab,out var bundle)?bundle:null;
    private static Dictionary<string,string> LoadCabBundles()
    {
        using var input=typeof(MonsterCatalog).Assembly.GetManifestResourceStream("LiveInteract.Contracts.bundle-cabs.json");
        using var reader=new StreamReader(input??throw new InvalidDataException("缺少原作资源引用目录"));
        return new Dictionary<string,string>(JsonConvert.DeserializeObject<Dictionary<string,string>>(reader.ReadToEnd())!,System.StringComparer.OrdinalIgnoreCase);
    }
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
        var monsters = JsonConvert.DeserializeObject<List<MonsterDefinition>>(reader.ReadToEnd())!;
        if (monsters.Any(m => !System.Enum.IsDefined(typeof(MonsterTier), m.Tier) || m.IsBoss != (m.Tier == MonsterTier.Boss)))
        {
            throw new InvalidDataException("怪物分类无效或与 Boss 标记不一致。");
        }
        SummonEligibility.Apply(monsters);
        return monsters;
    }
}
