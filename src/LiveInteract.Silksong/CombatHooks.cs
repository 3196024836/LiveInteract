using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>仅在敌人实际接受命中时应用临时玩家伤害倍率。</summary>
[HarmonyPatch(typeof(HealthManager), "TakeDamage")]
internal static class EnemyDamageHook
{
    /// <summary>修改当前命中数据，不永久覆盖武器配置。</summary>
    /// <param name="__0">实际命中结构。</param>
    private static bool Prefix(HealthManager __instance,ref HitInstance __0)
    {if(__instance.GetComponentInParent<OwnedCompanion>()!=null)return !NativeAllyExecution.IsHeroHit(__0);GameActions.Current?.AdjustHit(ref __0);return true;}
}

/// <summary>拦截正常角色受伤入口，维护临时免死与承伤倍率。</summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeroDamageHook
{
    /// <summary>只调整正常受伤参数，不代替剧情或坠落死亡。</summary>
    /// <param name="__state">是否建立了实际扣血的上下文。</param>
    /// <returns>是否继续原方法。</returns>
    [ThreadStatic] internal static int DamageDepth;
    private static bool Prefix(GameObject __0,out bool __state)
    {
        // 友军（含远程投射物）的命中一律不结算，并记一条诊断，方便实机确认有没有漏网来源。
        if(NativeAllyExecution.IsFriendly(__0)){GameActions.Current?.ReportFriendlyDamageBlocked(__0);__state=false;return false;}
        __state = GameActions.Current != null && !GameActions.Current.Invincible;
        if (GameActions.Current?.Invincible == true) { return false; }
        if (__state) { DamageDepth++; }
        return true;
    }

    /// <summary>无论受伤入口正常返回还是异常，都结束本次伤害上下文。</summary>
    private static void Finalizer(bool __state) { if (__state) { DamageDepth--; } }
}

/// <summary>仅在原生角色受伤实际扣血时处理倍率、护盾和免死，避免无敌帧白白消耗护盾。</summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.TakeHealth), new[] { typeof(int), typeof(bool), typeof(bool) })]
internal static class ActualHealthDamageHook
{
    private static bool Prefix(PlayerData __instance, ref int __0)
    {
        var actions = GameActions.Current;
        if (HeroDamageHook.DamageDepth <= 0 || actions == null) { return true; }
        __0 = (int)Math.Min(100000000, Math.Max(0, Math.Ceiling(__0 * actions.IncomingDamage)));
        __0 = actions.AbsorbDamage(__0);
        if (actions.Survive) { __0 = Math.Min(__0, Math.Max(0, __instance.health - 1)); }
        return __0 > 0;
    }
}

/// <summary>在角色配置读取时调整攻击节奏，避免污染纹章资产。</summary>
[HarmonyPatch]
internal static class AttackSpeedHook
{
    /// <summary>选取已在实际程序集验证存在的攻击时长读取器。</summary>
    /// <returns>需要插入补丁的固定方法。</returns>
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "AttackDuration", "AttackRecoveryTime", "AttackCooldownTime", "QuickAttackCooldownTime" })
        {
            yield return AccessTools.PropertyGetter(typeof(HeroControllerConfig), name);
        }
    }

    /// <summary>按当前临时倍率缩放攻击持续时间。</summary>
    /// <param name="__result">原本配置时间。</param>
    private static void Postfix(ref float __result) => __result /= (float)(GameActions.Current?.AttackSpeed ?? 1);
}

/// <summary>适配丝之歌实际使用的 tk2d 动画系统，避免只支持 Unity Animator 导致多数敌人失败。</summary>
[HarmonyPatch(typeof(tk2dSpriteAnimator), "UpdateAnimation")]
internal static class EnemyAnimationRateHook
{
    private static void Prefix(tk2dSpriteAnimator __instance, ref float __0)
    {
        if (GameActions.Current != null) { __0 *= GameActions.Current.AnimationSpeed(__instance); }
    }
}

/// <summary>本工具援军不因原生工具耗损或角色死亡提前消失；主动清理仍直接销毁。</summary>
[HarmonyPatch(typeof(ClockworkHatchling), nameof(ClockworkHatchling.Break))]
internal static class CompanionPersistenceHook
{
    private static bool Prefix(ClockworkHatchling __instance) => __instance.GetComponent<OwnedCompanion>() == null;
}

/// <summary>持续援军在读档/主菜单/暂停时不查找不存在的角色；不修改原生工具随从。</summary>
[HarmonyPatch]
internal static class CompanionReadyHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ClockworkHatchling), "Update");
        yield return AccessTools.Method(typeof(ClockworkHatchling), "FixedUpdate");
    }
    private static bool Prefix(ClockworkHatchling __instance) => __instance.GetComponent<OwnedCompanion>() == null || GameActions.Current?.CanAct == true;
}
