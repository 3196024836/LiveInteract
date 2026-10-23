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
    public static string RejectionReason(MonsterDefinition monster)
    {
        if (!monster.CanFight) { return monster.Name + "没有攻击动作，不支持召唤；本次没有生成替代单位。"; }
        return monster.SummonEligible ? "" : monster.Name + "不可召唤：" + monster.SummonExclusionReason + " 本次没有生成替代单位。";
    }

    /// <summary>所有已解析来源的具名生成、旧生成与转友军入口共用此校验。</summary>
    public static void Require(MonsterDefinition monster)
    {
        var reason = RejectionReason(monster);
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
        if (monsters.Any(m => approved.Contains(m.Id) && (!m.CanFight || review.ExclusionReasons.ContainsKey(m.Id))))
        { throw new InvalidDataException("召唤批准清单不能同时批准无攻击或明确排除的单位。"); }
        foreach (var monster in monsters)
        {
            monster.SummonEligible = approved.Contains(monster.Id);
            monster.SummonExclusionReason = monster.SummonEligible ? "" : review.ExclusionReasons.TryGetValue(monster.Id, out var reason) ? reason : "尚未通过本轮完整实机验收，暂不开放召唤。";
        }
    }

    private sealed class Review
    {
        [JsonProperty(Required = Required.Always)]
        public List<string> ApprovedIds { get; set; } = new();
        public Dictionary<string, string> ExclusionReasons { get; set; } = new();
    }
}
