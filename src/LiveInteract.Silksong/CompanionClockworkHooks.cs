using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>原生机械小弟保留自己的跟随/攻击逻辑，仅限制同屏距离传送与友方目标。</summary>
[HarmonyPatch(typeof(ClockworkHatchling), "GetHeroDistance")]
internal static class CompanionClockworkRecallHook
{
    private static void Postfix(ClockworkHatchling __instance, ref float __result)
    {
        var ally = __instance.GetComponent<NativeEnemyAlly>();
        if (ally != null && ally.IsOnScreen)
        {
            __result = Mathf.Min(__result, 8f);
        }
    }
}

[HarmonyPatch]
internal static class CompanionClockworkTargetHook
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(ClockworkHatchling), "<Start>b__91_0");
    private static bool Prefix(ClockworkHatchling __instance, Collider2D __0)
    {
        if (__instance.GetComponent<OwnedCompanion>() == null)
        {
            return true;
        }
        return __0 != null && __0.GetComponentInParent<OwnedCompanion>() == null
            && __0.GetComponentInParent<HeroController>() == null;
    }
}

