using UnityEngine;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>保存主播最近一次有效命中的敌人；只发布目标，不中断任何原作招式。</summary>
internal static class CompanionFocus
{
    // Unity 对象失效或离开作战半径后自动失去点名资格。
    private static HealthManager? designated;

    /// <summary>收到实际扣血证据后记录主播目标。</summary>
    /// <param name="target">接受真实伤害的敌人。</param>
    internal static void Designate(HealthManager target)
    {
        if (target != null && !NativeAllyExecution.IsFriendly(target.gameObject) && target.GetComponentInParent<HeroController>() == null)
            designated = target;
    }

    /// <summary>只向仍在有效作战范围的友军提供点名。</summary>
    /// <param name="position">友军位置。</param>
    /// <returns>当前有效点名，没有则为空。</returns>
    internal static HealthManager? For(Vector3 position)
    {
        // 距离按游戏物理平面计算，不受绘制深度影响。
        var hero = HeroController.SilentInstance;
        if (designated == null || hero == null) return null;
        return CompanionFollowPolicy.KeepTarget(designated.gameObject.activeInHierarchy && !designated.isDead && designated.hp > 0,
            NativeAllyExecution.IsFriendly(designated.gameObject), designated.GetComponentInParent<HeroController>() != null,
            Vector2.Distance(position, designated.transform.position), Vector2.Distance(hero.transform.position, designated.transform.position)) ? designated : null;
    }
}

/// <summary>在原作承伤入口前保存来源，实际扣血后才点名；友军及其派生物不会反向刷新。</summary>
[HarmonyLib.HarmonyPatch(typeof(HealthManager), "TakeDamage")]
internal static class PlayerDesignationHook
{
    /// <summary>先于原生执行上下文覆盖，采集真实命中来源。</summary>
    /// <param name="__instance">承伤敌人。</param>
    /// <param name="__0">原始命中。</param>
    /// <param name="__state">命中前生命及点名资格。</param>
    [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
    private static void Prefix(HealthManager __instance, HitInstance __0, out (int Hp, bool Player, bool Friendly, bool Flow) __state)
        => __state = (__instance.hp, __0.IsHeroDamage || __0.IsNailTag || __0.Source != null && __0.Source.GetComponentInParent<HeroController>() != null,
            NativeAllyExecution.IsFriendly(__0.Source), NativeAllyExecution.IsFriendlyFlow || NativeAllyExecution.Current != null);

    /// <summary>只计入原作实际接受的伤害。</summary>
    /// <param name="__instance">承伤敌人。</param>
    /// <param name="__state">命中前的证据。</param>
    private static void Postfix(HealthManager __instance, (int Hp, bool Player, bool Friendly, bool Flow) __state)
    {
        if (__instance != null && CompanionCombatPolicy.IsPlayerDesignation(__state.Hp, __instance.hp, __state.Player, __state.Friendly, __state.Flow))
            CompanionFocus.Designate(__instance);
    }
}
