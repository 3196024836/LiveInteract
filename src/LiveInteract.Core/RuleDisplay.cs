using LiveInteract.Contracts;

namespace LiveInteract.Core;

/// <summary>用同一份效果参数生成规则列表与编辑预览，避免只显示笼统动作名称。</summary>
public static class RuleDisplay
{
    /// <summary>描述一次触发会执行的效果和实际数值。</summary>
    /// <param name="step">效果。</param>
    public static string Effect(ActionStep step)
    {
        if (step.Kind != StepKind.Action) { return "旧版组合 · " + step.Children.Count + " 项（原样保留）"; }
        if(step.ActionId=="ally.copy-next")return "永久复制下次遇敌为友军 · 无目标保留";
        var descriptor = ActionCatalog.All.FirstOrDefault(item => item.Id == step.ActionId);
        var name = descriptor?.Name ?? step.ActionId;
        string Value(string key) => step.Arguments.GetValueOrDefault(key) ?? descriptor?.Parameters.FirstOrDefault(p => p.Key == key)?.DefaultValue ?? "";
        if(SummonCommands.IsSummon(step.ActionId) && descriptor?.Parameters.Any(p=>p.Key=="faction")==true)
            name=(SummonCommands.IsFriendly(step.ActionId,step.Arguments)?"友方 ":"敌方 ")+name.Replace("友方","").Replace("敌方","");
        if (descriptor?.Parameters.Any(p => p.Key == "count") == true) { name += " ×" + Value("count"); }
        else if (descriptor?.Parameters.Any(p => p.Key == "value") == true)
        {
            name += " " + Value("value") + (step.ActionId == "health.blue" ? " 点" : step.ActionId.StartsWith("health.") ? " 格" : step.ActionId.StartsWith("silk.") || step.ActionId is "boss.heal" or "boss.damage" or "enemy.heal" or "enemy.damage" ? " 点" : "");
        }
        if (step.ActionId is "summon.random" or "summon.random.ally" or "summon.random.enemy")
        {
            name += " · " + RandomPool(Value("pool"));
            if (double.TryParse(Value("power"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var strength) && strength != 1)
            {
                name += " · 强度 " + strength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
            }
        }
        if (step.ActionId.StartsWith("ally.", StringComparison.Ordinal)) { name += Value("lifetime") == "0" ? " · 持续跟随" : " · " + Value("lifetime") + " 秒"; }
        if (descriptor?.IsTemporary == true) { name += step.DurationSeconds == 0 ? " · 永久" : " · " + step.DurationSeconds + " 秒"; }
        var monster=MonsterCatalog.Find(step.ActionId);
        if(monster==null&&NativeSummons.Find(step.ActionId) is {} ally) { monster=MonsterCatalog.Find(ally.MonsterId); }
        if(monster==null&&step.ActionId is "summon.named" or "summon.named.ally" or "summon.named.enemy") { monster=SummonCommands.FindSource(Value("name")); }
        if(monster==null&&step.ActionId is "enemy.spawn" or "boss.clone" or "enemy.asset" or "boss.asset") { monster=SummonCommands.FindSource(Value(step.ActionId is "enemy.asset" or "boss.asset"?"asset":"enemy")); }
        if(monster!=null&&SummonEligibility.RejectionReason(monster,SummonCommands.IsFriendly(step.ActionId,step.Arguments)) is { Length:>0 } rejection) { name+=" · "+rejection; }
        return name;
    }

    /// <summary>旧配置的池名必须直接显出其与新分档池的差别。</summary>
    public static string RandomPool(string pool) => pool switch
    {
        "weak" => "弱怪池",
        "strongBoss" => "强怪 + Boss池",
        "ordinary" => "旧版普通池（弱怪 + 强怪，无Boss）",
        "all" => "旧版全池（弱怪 + 强怪 + Boss）",
        _ => "未知池（" + pool + "）"
    };

    private static string GiftRange(TriggerDefinition t) => t.FreeOrUnknownGiftOnly ? "（零价 / 单价未知）" : t.MinGiftUnitValue == 0 && t.MaxGiftUnitValue == 0 ? "" : "（" + (t.MaxGiftUnitValue == 0 ? "≥" + t.MinGiftUnitValue : t.MinGiftUnitValue + "–" + t.MaxGiftUnitValue) + "钻）";

    /// <summary>描述一个触发来源与单位。</summary>
    /// <param name="trigger">触发配置。</param>
    public static string Trigger(TriggerDefinition trigger) => trigger.Type switch
    {
        LiveEventType.Gift => "每 " + trigger.Threshold + " 个" + (trigger.GiftName.Length > 0 ? trigger.GiftName : trigger.GiftId.Length > 0 ? "指定礼物" : "任意礼物") + GiftRange(trigger),
        LiveEventType.Like => "累计 " + trigger.Threshold + " 次点赞",
        LiveEventType.Chat => "弹幕" + (trigger.Keywords.Count > 0 ? "「" + string.Join(" / ", trigger.Keywords) + "」" : "（任意内容）") + (trigger.Threshold > 1 ? " 每 " + trigger.Threshold + " 条" : ""),
        LiveEventType.Follow => "每 " + trigger.Threshold + " 次关注",
        LiveEventType.Enter => "每 " + trigger.Threshold + " 次进场",
        LiveEventType.Share => "每 " + trigger.Threshold + " 次分享",
        _ => "每 " + trigger.Threshold + " 次粉丝团互动"
    };
}
