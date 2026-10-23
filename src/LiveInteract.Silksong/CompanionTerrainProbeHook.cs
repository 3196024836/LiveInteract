using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using LiveInteract.Contracts;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>原作墙体射线只在友军执行中忽略敌人层，仍保留真实墙、地形和原作事件。</summary>
[HarmonyPatch(typeof(RayCast2dV2), "DoRaycast")]
internal static class FriendlyTerrainProbeHook
{
#if LIVEINTERACT_QA
    // 隔离A/B诊断开关；正式构建始终启用修复。
    internal static bool Enabled = true;
#endif

    /// <summary>在原作层掩码转换后过滤本次结果，不改变射线和事件执行。</summary>
    /// <param name="instructions">原作射线方法指令。</param>
    /// <returns>只附加本次掩码过滤的指令。</returns>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        // 原作转换入口和本地过滤入口；保留原指令及其标签。
        var convert = AccessTools.Method(typeof(ActionHelpers), nameof(ActionHelpers.LayerArrayToLayerMask));
        var filter = AccessTools.Method(typeof(FriendlyTerrainProbeHook), nameof(FilterMask));
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is MethodInfo method && method == convert)
            {
                // 过滤的是本次调用的整型结果，不修改共享 FSM 的层数组或预制体。
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, filter);
            }
        }
    }

    /// <summary>最终之爪实体撞到矮墙而中心射线越过墙顶时，以真实侧面碰撞完成原作收招。</summary>
    private static void Postfix(RayCast2dV2 __instance)
    {
#if LIVEINTERACT_QA
        if (!Enabled) return;
#endif
        var ally = NativeAllyExecution.Current;
        if (!NativeAllyExecution.IsFriendlyFlow || ally == null || ally.NativeFriendlyAi ||
            __instance.storeDidHit.Value || __instance.invertMask.Value ||
            !CompanionTerrainProbe.UsesSolidChargeProbe(ally.GetComponent<OwnedSummon>()?.AssetKey ?? "",
                __instance.Fsm.Name, __instance.Fsm.ActiveState?.Name ?? "", __instance.hitEvent?.Name ?? "")) return;
        var terrainLayer = (int)GlobalEnums.PhysLayers.TERRAIN;
        if (ActionHelpers.LayerArrayToLayerMask(__instance.layerMask, false) != 1 << terrainLayer) return;
        var origin = AccessTools.Field(typeof(RayCast2dV2), "_trans").GetValue(__instance) as Transform;
        if (origin == null || origin.GetComponentInParent<NativeEnemyAlly>() != ally) return;
        var direction = __instance.direction.Value.normalized;
        if (__instance.space == Space.Self)
            direction = origin.TransformDirection(new Vector3(direction.x, direction.y, 0)).normalized;
        if (Mathf.Abs(direction.y) > .01f || Mathf.Abs(direction.x) < .99f ||
            !ally.TryGetSolidWallImpact(direction, out var hit)) return;
        // 与原动作采用相同记录字段和原END事件；依据是实体地形碰撞，不是超时、扣血或强设攻击态。
        PlayMakerUnity2d.RecordLastRaycastHitInfo(__instance.Fsm, hit);
        __instance.storeDidHit.Value = true;
        __instance.storeHitObject.Value = hit.collider.gameObject;
        __instance.storeHitPoint.Value = hit.point;
        __instance.storeHitNormal.Value = hit.normal;
        __instance.storeHitDistance.Value = hit.fraction;
        __instance.storeDistance.Value = hit.distance;
#if LIVEINTERACT_QA
        QaHarness.RecordActor(ally.gameObject, "native-solid-wall-impact:" + hit.collider.name);
#endif
        __instance.Fsm.Event(__instance.hitEvent);
    }

    /// <summary>仅在改编友军的非反选墙体射线中排除敌人层。</summary>
    /// <param name="original">原作本次生成的层掩码。</param>
    /// <param name="action">发出射线的原作动作。</param>
    /// <returns>仍包含真实地形的本次掩码。</returns>
    private static int FilterMask(int original, RayCast2dV2 action)
    {
#if LIVEINTERACT_QA
        if (!Enabled) { return original; }
#endif
        // 原生机械小弟和敌方不经友军改编过滤。
        var ally = NativeAllyExecution.Current;
        var friendly = NativeAllyExecution.IsFriendlyFlow && ally != null && !ally.NativeFriendlyAi;
        if (!friendly || action.invertMask.Value) { return original; }
        return CompanionTerrainProbe.FilterMask(original, (int)GlobalEnums.PhysLayers.TERRAIN,
            (int)GlobalEnums.PhysLayers.ENEMIES, true, action.hitEvent?.Name ?? "");
    }
}
