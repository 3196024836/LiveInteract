using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiveInteract.Contracts;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>仅在敌人实际接受命中时应用临时玩家伤害倍率。</summary>
[HarmonyPatch(typeof(HealthManager), "TakeDamage")]
internal static class EnemyDamageHook
{
    /// <summary>修改当前命中数据，不永久覆盖武器配置。</summary>
    /// <param name="__0">实际命中结构。</param>
    private static bool Prefix(HealthManager __instance,ref HitInstance __0)
    {if(__instance.GetComponentInParent<OwnedCompanion>()!=null)return !NativeAllyExecution.ProtectFriendlyHit(__instance,__0);GameActions.Current?.AdjustHit(ref __0);return true;}
}

/// <summary>玩家/友军的命中在原作受击事件前结束，不能只拦扣血而仍触发友军对玩家的反击索敌。</summary>
[HarmonyPatch(typeof(HealthManager),"Hit",new[]{typeof(HitInstance)})]
internal static class FriendlyHitImmunityHook
{
    // 原生执行来源前缀先入栈，最后拦截，保证被取消的命中也能正确恢复嵌套来源。
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(HealthManager __instance,HitInstance __0)=>
        !NativeAllyExecution.ProtectFriendlyHit(__instance,__0);
}

/// <summary>拦截正常角色受伤入口，维护临时免死与承伤倍率。</summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeroDamageHook
{
    /// <summary>只调整正常受伤参数，不代替剧情或坠落死亡。</summary>
    /// <param name="__state">是否建立了实际扣血的上下文。</param>
    /// <returns>是否继续原方法。</returns>
    [ThreadStatic] internal static int DamageDepth;
    private static bool Prefix(GameObject __0,int __2,out bool __state)
    {
        // 友军（含远程投射物）的命中一律不结算，并记一条诊断，方便实机确认有没有漏网来源。
        if(NativeAllyExecution.IsFriendly(__0)||NativeAllyExecution.IsFriendlyFlow||NativeAllyExecution.Current!=null)
        {
            var ally=NativeAllyExecution.Current??__0?.GetComponentInParent<NativeEnemyAlly>()??__0?.GetComponentInParent<NativeAllyProjectile>()?.Owner;
            if(ally!=null)ally.StrikeDirect(Math.Max(0,__2),__0??ally.gameObject);
            GameActions.Current?.ReportFriendlyDamageBlocked(__0!);__state=false;return false;
        }
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
        if(NativeAllyExecution.IsFriendlyFlow||NativeAllyExecution.Current!=null)return false;
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

/// <summary>原作有绕过 TakeDamage 的快速/特殊扣血入口；友军状态机的反射调用也在造成硬直前被拦截。</summary>
[HarmonyPatch]
internal static class FriendlySpecialDamageHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>typeof(HeroController).GetMethods(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Where(m=>m.Name is "TakeQuickDamage" or "DoSpecialDamage" or "TakeHealth");
    private static bool Prefix(int __0,System.Reflection.MethodBase __originalMethod,out bool __state)
    {
        __state=false;var ally=NativeAllyExecution.Current;
        if(ally!=null){ally.StrikeDirect(Math.Max(0,__0),ally.gameObject);return false;}
        if(NativeAllyExecution.IsFriendlyFlow)return false;
        if(__originalMethod.Name!="TakeHealth"&&GameActions.Current?.Invincible==true)return false;
        if(__originalMethod.Name=="DoSpecialDamage"){HeroDamageHook.DamageDepth++;__state=true;}
        return true;
    }
    private static void Finalizer(bool __state){if(__state)HeroDamageHook.DamageDepth--;}
}

/// <summary>无尸体的原作单位不能在禁用剧情掉落时解引用空尸体；仅处理本工具副本。</summary>
[HarmonyPatch(typeof(EnemyDeathEffects),"RemoveQuestDropsFromGameObject")]
internal static class SummonEmptyCorpseHook
{
    private static bool Prefix(EnemyDeathEffects __instance,GameObject __0)=>__0!=null||__instance.GetComponentInParent<OwnedSummon>()==null;
}

/// <summary>召唤物不能沿用原作战斗结束/剧情入口，把主播送到其他房间。</summary>
[HarmonyPatch(typeof(GameManager),nameof(GameManager.BeginSceneTransition))]
internal static class SummonSceneTransitionHook
{
    private static bool Prefix(System.Reflection.MethodBase __originalMethod)
    {
        if(!NativeAllyExecution.InSpawnedFlow)return true;
        NativeAllyExecution.NotifyStoryBlocked(__originalMethod.DeclaringType!.Name+"."+__originalMethod.Name);return false;
    }
}
/// <summary>原作结局动作直接写PlayerData字段，不经过SetBool；召唤副本必须在动作入口隔离。</summary>
[HarmonyPatch]
internal static class SummonEndingActionHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SetEndingCompleted),"OnEnter");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.StartCinematic),"OnEnter");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SetCinematicVideo),"OnEnter");
        yield return AccessTools.Method(typeof(ReportGameEnding),"OnEnter");
    }
    private static bool Prefix(HutongGames.PlayMaker.FsmStateAction __instance)
    {
        if(!NativeAllyExecution.InSpawnedFlow&&__instance.Owner?.GetComponentInParent<OwnedSummon>()==null)return true;
        NativeAllyExecution.NotifyStoryBlocked(__instance.GetType().Name);
        if(__instance.Fsm!=null)__instance.Finish();
        return false;
    }
}

/// <summary>LoadScene与视频入口不经过BeginSceneTransition；仅拦截召唤来源，剧情正常执行。</summary>
[HarmonyPatch]
internal static class SummonCinematicEntryHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GameManager),"LoadScene",new[]{typeof(string)});
        yield return AccessTools.Method(typeof(GameManager),"LoadOpeningCinematic");
        yield return AccessTools.Method(typeof(GameManager),"RecordGameComplete");
        yield return AccessTools.Method(typeof(CinematicPlayer),"TriggerStartVideo");
    }
    private static bool Prefix(System.Reflection.MethodBase __originalMethod)
    {
        if(!NativeAllyExecution.InSpawnedFlow)return true;
        NativeAllyExecution.NotifyStoryBlocked(__originalMethod.DeclaringType!.Name+"."+__originalMethod.Name);return false;
    }
}

/// <summary>副本销毁后注销原作抖动渲染回调，避免留下逐帧空引用。</summary>
[HarmonyPatch]
internal static class SummonJitterLifetimeHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.ObjectJitterOnRender),"OnCameraPreCull");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.ObjectJitterOnRender),"OnCameraPostRender");
    }
    private static bool Prefix(HutongGames.PlayMaker.Actions.ObjectJitterOnRender __instance,GameObject ___go)
    {
        if(___go!=null)return true;__instance.OnExit();return false;
    }
}

/// <summary>召唤 Boss 死亡不触发全局慢镜头；仍保留原作模型、攻击和死亡表现。</summary>
[HarmonyPatch]
internal static class SummonDeathTimeScaleHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>typeof(GameManager).GetMethods(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Where(m=>m.Name=="SetTimeScale");
    private static bool Prefix()=>!NativeAllyExecution.InSpawnedFlow;
}

/// <summary>召唤Boss尸体的延迟FreezeMoment组件不能在下一帧脱离来源后触发全局慢镜头。</summary>
[HarmonyPatch]
internal static class SummonFreezeEffectHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(FreezeMomentOnEnable),"OnEnable");
        yield return AccessTools.Method(typeof(FreezeMomentOnEnable),"DoFreeze");
    }
    private static bool Prefix(FreezeMomentOnEnable __instance,ref bool ___scheduledFreeze)
    {
        if(!NativeAllyExecution.InSpawnedFlow&&__instance.GetComponentInParent<OwnedSummon>()==null&&
            string.IsNullOrEmpty(__instance.GetComponentInParent<OwnedTransientResource>()?.AssetKey))return true;
        ___scheduledFreeze=false;return false;
    }
}

/// <summary>原作状态机继续读写召唤物自己的临时状态，不访问已移除的世界存档组件。</summary>
[HarmonyPatch]
internal static class SummonLocalPersistentStateHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SetPersistentInt),"OnEnter");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.GetPersistentInt),"OnEnter");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SetPersistentBool),"OnEnter");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.GetPersistentBool),"OnEnter");
    }
    private static bool Prefix(HutongGames.PlayMaker.FsmStateAction __instance)
    {
        var local=__instance.Owner?.GetComponentInParent<OwnedSummon>();if(local==null)return true;
        switch(__instance)
        {
            case HutongGames.PlayMaker.Actions.SetPersistentInt set:
                local.LocalInts[(__instance.Fsm.GetOwnerDefaultTarget(set.target)??local.gameObject).GetInstanceID()]=set.setValue.Value;break;
            case HutongGames.PlayMaker.Actions.GetPersistentInt get:
                get.storeValue.Value=local.LocalInts.TryGetValue((__instance.Fsm.GetOwnerDefaultTarget(get.target)??local.gameObject).GetInstanceID(),out var integer)?integer:0;break;
            case HutongGames.PlayMaker.Actions.SetPersistentBool set:
                local.LocalBools[(__instance.Fsm.GetOwnerDefaultTarget(set.Target)??local.gameObject).GetInstanceID()]=set.SetValue.Value;break;
            case HutongGames.PlayMaker.Actions.GetPersistentBool get:
                get.storeValue.Value=local.LocalBools.TryGetValue((__instance.Fsm.GetOwnerDefaultTarget(get.target)??local.gameObject).GetInstanceID(),out var boolean)&&boolean;break;
        }
        __instance.Finish();return false;
    }
}

[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.ObjectJitterOnRender),"OnEnter")]
internal static class SummonJitterRegistrationHook
{
    private static void Postfix(HutongGames.PlayMaker.Actions.ObjectJitterOnRender __instance)
    {__instance.Owner?.GetComponentInParent<OwnedSummon>()?.ActiveJitters.Add(__instance);}
}
/// <summary>原作视野组件识别友方当前敌人，不再等待主播碰进 Hero 专用感应层。</summary>
[HarmonyPatch(typeof(AlertRange),nameof(AlertRange.IsHeroInRange))]
internal static class FriendlyAlertRangeHook
{
    private static bool Prefix(AlertRange __instance,ref bool __result)
    {
        var ally=__instance.GetComponentInParent<NativeEnemyAlly>()??NativeAllyExecution.Current;if(ally==null)return true;
        __result=ally.TargetInRange(__instance);return false;
    }
}
[HarmonyPatch(typeof(AlertRange),nameof(AlertRange.GetUnalertTime))]
internal static class FriendlyAlertTimeoutHook
{
    private static bool Prefix(AlertRange __instance,ref float __result)
    {var ally=__instance.GetComponentInParent<NativeEnemyAlly>()??NativeAllyExecution.Current;if(ally==null)return true;__result=ally.HasEnemy?0:100000;return false;}
}
[HarmonyPatch]
internal static class FriendlyLineOfSightHook
{
    private static System.Reflection.MethodBase TargetMethod()=>AccessTools.PropertyGetter(typeof(LineOfSightDetector),nameof(LineOfSightDetector.CanSeeHero));
    private static bool Prefix(LineOfSightDetector __instance,ref bool __result)
    {var ally=__instance.GetComponentInParent<NativeEnemyAlly>()??NativeAllyExecution.Current;if(ally==null)return true;__result=ally.HasEnemy;return false;}
}

/// <summary>场景脚本的“已死亡”开关不能让刚召唤且仍有生命的副本失效；实际扣血到零仍正常死亡。</summary>
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.SetDead))]
internal static class SummonAliveStateHook
{
    private static bool Prefix(HealthManager __instance)=>__instance.GetComponentInParent<OwnedSummon>()==null||__instance.hp<=0;
}
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.SetIsDead))]
internal static class SummonAliveStateValueHook
{
    private static bool Prefix(HealthManager __instance,bool __0)=>!__0||__instance.GetComponentInParent<OwnedSummon>()==null||__instance.hp<=0;
}

/// <summary>为场景提取流提供有界、可复用的拷贝缓冲，避免 Mono 中反复分配大数组。</summary>
[HarmonyPatch(typeof(AssetsTools.NET.Extra.Net35Polyfill),nameof(AssetsTools.NET.Extra.Net35Polyfill.CopyToCompat))]
internal static class SceneRepackBufferPoolHook
{
    private static bool Prefix(System.IO.Stream __0,System.IO.Stream __1,long __2,int __3)
    {
        var buffer=System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1024,__3));
        try
        {
            var remaining=__2<0?long.MaxValue:__2;
            while(remaining>0)
            {
                var read=__0.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(read<=0)break;
                __1.Write(buffer,0,read);remaining-=read;
            }
        }
        finally{System.Buffers.ArrayPool<byte>.Shared.Return(buffer);}
        return false;
    }
}

/// <summary>原作吊挂仆从在原房间落地会被清场；独立召唤后不沿用这一场景清理事件。</summary>
[HarmonyPatch]
internal static class SummonWeaverTerrainLifetimeHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Collision2dEvent),"DoCollisionEnter2D");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Collision2dEvent),"DoCollisionStay2D");
    }
    private static bool Prefix(HutongGames.PlayMaker.Actions.Collision2dEvent __instance,Collision2D __0)
    {
        var owned=__instance.Owner?.GetComponentInParent<OwnedSummon>();
        if(owned==null||owned.AssetKey is not ("spawn.weaver-servitor-large" or "spawn.weaver-servitor"))return true;
        return __0.gameObject.layer!=8;
    }
}

/// <summary>友方感应列表由真实敌人填充，不能被只接受 Hero 层的原生刷新覆盖。</summary>
[HarmonyPatch(typeof(TrackTriggerObjects),"GetOverlappedColliders")]
internal static class FriendlyTriggerPopulationHook
{
    private static bool Prefix(TrackTriggerObjects __instance)=>__instance.gameObject.layer!=13||__instance.GetComponentInParent<NativeEnemyAlly>()==null;
}
[HarmonyPatch(typeof(TrackTriggerObjects),"IsCounted")]
internal static class FriendlyTriggerTargetCountHook
{
    private static bool Prefix(TrackTriggerObjects __instance,GameObject __0,ref bool __result)
    {
        var ally=__instance.GetComponentInParent<NativeEnemyAlly>();if(ally==null||__instance.gameObject.layer!=13)return true;
        __result=ally.HasEnemy&&__0==ally.TargetObject;return false;
    }
}

/// <summary>按名字查找视野时限定到该召唤物，不能误取场上其他同名单位的感应区。</summary>
[HarmonyPatch]
internal static class FriendlyNamedAlertRangeHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(CheckAlertRangeByName),"OnEnter");yield return AccessTools.Method(typeof(CheckAlertRangeByName),"OnUpdate");}
    private static bool Prefix(CheckAlertRangeByName __instance)
    {
        var ally=__instance.Owner?.GetComponentInParent<NativeEnemyAlly>();if(ally==null)return true;
        var range=ally.GetComponentsInChildren<AlertRange>(true).FirstOrDefault(r=>r.name==__instance.alertRangeName);
        var inside=ally.TargetInRange(range);
        __instance.storeResult.Value=inside;var ev=inside?__instance.sendEvent:__instance.outOfRangeEvent;
        // 塞斯Idle会在OnEnter立刻把没有敌人当作Throw Range外并传送，早于逐帧随行接管。
        // 只抑制这一已核对的无敌开战事件；出招收尾和真实敌人在范围外仍按原作处理。
        if(!ev.IsNone&&!string.IsNullOrWhiteSpace(ev.Value)&&CompanionCombatPolicy.ShouldEmitNamedRangeEvent(
            ally.GetComponent<OwnedSummon>()?.AssetKey??"",__instance.Fsm.Name,__instance.State.Name,
            __instance.alertRangeName,ev.Value,ally.HasEnemy))__instance.Fsm.Event(__instance.eventTarget,ev.Value);
        if(!__instance.everyFrame)__instance.Finish();return false;
    }
}

/// <summary>友军的原作吼叫/抓取状态不能夺走主播的操作；敌方正常招式不受影响。</summary>
[HarmonyPatch]
internal static class FriendlyPlayerControlHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(HeroController),nameof(HeroController.RelinquishControl));yield return AccessTools.Method(typeof(HeroController),nameof(HeroController.IgnoreInput));}
    private static bool Prefix()=>!NativeAllyExecution.IsFriendlyFlow;
}

/// <summary>友方保留原作吼叫动画和特效，仅关闭本次动作对主播的吼叫锁定。</summary>
[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.StartRoarEmitter),"StartRoar")]
internal static class FriendlyRoarStunHook
{

    /// <summary>临时替换动作参数，不改写共享FSM变量或敌方的原作配置。</summary>
    /// <param name="__instance">正在发动吼叫的原作动作。</param>
    /// <param name="__state">调用结束后需恢复的原参数引用。</param>
    private static void Prefix(HutongGames.PlayMaker.Actions.StartRoarEmitter __instance,out HutongGames.PlayMaker.FsmBool? __state)
    {
        __state=null;
        if(!NativeAllyExecution.IsFriendly(__instance.Owner)&&!NativeAllyExecution.IsFriendlyFlow)return;
        // START/BURST特效及原作动作完成事件继续执行，仅禁用玩家控制副作用。
        __state=__instance.stunHero;
        __instance.stunHero=new HutongGames.PlayMaker.FsmBool{Value=false};
    }

    /// <summary>包括异常退出在内，恢复该动作的原参数引用。</summary>
    /// <param name="__instance">吼叫动作。</param>
    /// <param name="__state">进入时保留的原参数引用。</param>
    private static void Finalizer(HutongGames.PlayMaker.Actions.StartRoarEmitter __instance,HutongGames.PlayMaker.FsmBool? __state)
    {
        if(__state!=null)__instance.stunHero=__state;
    }
}

/// <summary>原作机关每轮攻击会回满血；新加的可击杀召唤副本不能沿用这项免死机制。</summary>
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.RefillHP))]
internal static class SummonProxyHealthRefillHook
{
    private static bool Prefix(HealthManager __instance)
    {
        var actor=__instance.GetComponentInParent<OwnedSummon>();
        return actor==null||MonsterCatalog.Find(actor.AssetKey)?.AddNativeHealth!=true;
    }
}

/// <summary>抽取场景单位时，朝向动作不能再访问已卸载的原场景引用。</summary>
[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.MatchScaleSign),"DoAction")]
internal static class SummonScaleReferenceHook
{
    private static bool Prefix(HutongGames.PlayMaker.Actions.MatchScaleSign __instance,GameObject ___target)
    {
        var owner=__instance.Owner;
        var summoned=owner?.GetComponentInParent<OwnedSummon>();
        var effect=owner?.GetComponentInParent<OwnedTransientResource>();
        if(!NativeAllyExecution.InSpawnedFlow&&summoned==null&&string.IsNullOrEmpty(effect?.AssetKey))return true;
        if(___target==null){__instance.Finish();return false;}
        if(__instance.MatchTo.Value==null)__instance.MatchTo.Value=summoned!=null?summoned.gameObject:effect!=null?effect.gameObject:owner;
        return __instance.MatchTo.Value!=null;
    }
}

/// <summary>友军原作命中触发器忽略主播与友军，不能因贴近自己人提前进入连击或收招。</summary>
[HarmonyPatch]
internal static class FriendlyTriggerFactionHook
{
    /// <summary>仅过滤已核对的原作分层触发回调；退出、地形和敌方事件保持原样。</summary>
    /// <returns>友军接触命中会用到的入口。</returns>
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Trigger2dEventLayer), "DoTriggerEnter2D");
        yield return AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Trigger2dEventLayer), "DoTriggerStay2D");
    }

    /// <summary>以实际碰撞来源判断阵营，不修改原作层、事件名、动画或伤害数值。</summary>
    /// <param name="__instance">正在处理物理接触的原作动作。</param>
    /// <param name="__0">原生碰撞回调中的另一碰撞体。</param>
    /// <returns>阵营合法时继续执行原作事件。</returns>
    private static bool Prefix(HutongGames.PlayMaker.FsmStateAction __instance, Collider2D __0)
    {
        // 不接管原场景敌人，也不把地形、道具或敌方身体视作友军。
        if (__0 == null || __instance.Owner == null) { return true; }
        return !CompanionCombatPolicy.ShouldIgnoreFriendlyContactEvent(
            NativeAllyExecution.IsFriendly(__instance.Owner),
            __0.GetComponentInParent<HeroController>() != null,
            NativeAllyExecution.IsFriendly(__0.gameObject));
    }
}
