using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LiveInteract.Contracts;

/// <summary>本轮稳定性验收门控；与原作攻击能力、分档和旧随机限制分别判断。</summary>
public static class SummonEligibility
{
    /// <summary>可用时为空；拒绝时提供可直接展示给旧规则用户的原因。</summary>
    /// <param name="monster">待召唤单位。</param>
    /// <param name="friendly">按友军（真）或敌方（假）资格判断。</param>
    /// <returns>允许时为空串，否则为拒绝原因。</returns>
    public static string RejectionReason(MonsterDefinition monster, bool friendly = true)
    {
        if (!monster.CanFight) { return monster.Name + "没有攻击动作，不支持召唤；本次没有生成替代单位。"; }
        if (friendly ? monster.SummonEligible : monster.EnemySummonEligible) { return ""; }
        // 只开放了另一阵营时，说明当前阵营尚未验收，而不是笼统“不可召唤”。
        var reason = monster.EnemyOnlyEligible && friendly && monster.SummonExclusionReason.Length > 0 && !monster.SummonExclusionReason.StartsWith("尚未通过") ? monster.SummonExclusionReason
            : monster.SummonEligible || monster.EnemySummonEligible ? "尚未通过" + (friendly ? "友军" : "敌方") + "实机验收。" : monster.SummonExclusionReason;
        return monster.Name + (friendly ? "不可召唤（友军）：" : "不可召唤（敌方）：") + reason + " 本次没有生成替代单位。";
    }

    /// <summary>所有已解析来源的具名生成、旧生成与转友军入口共用此校验。</summary>
    /// <param name="monster">待召唤单位。</param>
    /// <param name="friendly">按友军（真）或敌方（假）资格判断。</param>
    public static void Require(MonsterDefinition monster, bool friendly = true)
    {
        var reason = RejectionReason(monster, friendly);
        if (reason.Length > 0) { throw new ArgumentException(reason, nameof(monster)); }
    }

    /// <summary>只从独立批准清单装载；未列出的234项归档默认关闭，不改写资源目录。</summary>
    internal static void Apply(IReadOnlyList<MonsterDefinition> monsters)
    {
        using var stream = typeof(SummonEligibility).Assembly.GetManifestResourceStream("LiveInteract.Contracts.summon-approvals.json");
        using var reader = new StreamReader(stream ?? throw new InvalidDataException("缺少召唤批准清单。"));
        var review = JsonConvert.DeserializeObject<Review>(reader.ReadToEnd()) ?? throw new InvalidDataException("召唤批准清单为空。");
        var ids = new HashSet<string>(monsters.Select(m => m.Id), StringComparer.Ordinal);
        if (review.ApprovedIds == null || review.ExclusionReasons == null || review.ApprovedIds.Distinct(StringComparer.Ordinal).Count() != review.ApprovedIds.Count ||
            review.ApprovedIds.Concat(review.ExclusionReasons.Keys).Any(id => !ids.Contains(id)) ||
            review.ExclusionReasons.Values.Any(string.IsNullOrWhiteSpace))
        { throw new InvalidDataException("召唤批准清单包含未知、重复或无效记录。"); }
        var approved = new HashSet<string>(review.ApprovedIds, StringComparer.Ordinal);
        // 不会跟随主播、但作为敌人能正常作战的单位单独列出，只进入敌方召唤。
        var enemyApproved = new HashSet<string>(review.EnemyOnlyIds, StringComparer.Ordinal);
        if (review.EnemyOnlyIds.Distinct(StringComparer.Ordinal).Count() != review.EnemyOnlyIds.Count || review.EnemyOnlyIds.Any(id => !ids.Contains(id) || approved.Contains(id)))
        { throw new InvalidDataException("仅敌方召唤清单包含未知、重复或已在友方白名单中的记录。"); }
        var allyOnly = new HashSet<string>(review.AllyOnlyIds, StringComparer.Ordinal);
        if (review.AllyOnlyIds.Distinct(StringComparer.Ordinal).Count() != review.AllyOnlyIds.Count || review.AllyOnlyIds.Any(id => !approved.Contains(id)))
        { throw new InvalidDataException("仅友方清单只能标记友方白名单内的单位且不得重复。"); }
        // 仅敌方单位可保留排除原因，用来说明友方为何未开放。
        if (monsters.Any(m => approved.Contains(m.Id) && (!m.CanFight || review.ExclusionReasons.ContainsKey(m.Id)) || enemyApproved.Contains(m.Id) && !m.CanFight))
        { throw new InvalidDataException("召唤批准清单不能同时批准无攻击或明确排除的单位。"); }
        foreach (var monster in monsters)
        {
            monster.EnemyOnlyEligible = enemyApproved.Contains(monster.Id);
            monster.AllyOnly = allyOnly.Contains(monster.Id);
            monster.SummonEligible = approved.Contains(monster.Id);
            monster.SummonExclusionReason = monster.SummonEligible ? "" : review.ExclusionReasons.TryGetValue(monster.Id, out var reason) ? reason : "尚未通过本轮完整实机验收，暂不开放召唤。";
        }
    }

    private sealed class Review
    {
        [JsonProperty(Required = Required.Always)]
        public List<string> ApprovedIds { get; set; } = new();
        public Dictionary<string, string> ExclusionReasons { get; set; } = new();
        // 可选：仅作为敌方开放的单位。
        public List<string> EnemyOnlyIds { get; set; } = new();
        // 可选：友方白名单中敌方筛测未过、不进入敌方召唤的单位。
        public List<string> AllyOnlyIds { get; set; } = new();
    }
}
