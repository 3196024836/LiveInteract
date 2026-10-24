#if LIVEINTERACT_QA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>隔离实机的短时耗时诊断；仅观察调用，不改攻击、生命或原作状态。</summary>
internal static class QaRuntimeTimings
{
    // 短时采样补丁与各方法的累计计数，停止后全部移除。
    private static readonly Harmony Hooks = new("cn.liveinteract.qa.timings");
    private static readonly Dictionary<MethodBase, (long Calls, long Ticks, long Max)> Counters = new();
    private static readonly List<JObject> HitEvents = new();
    private static readonly Dictionary<MethodBase, long> NativeCalls = new();

    /// <summary>按原作方法计数，定位频繁进入上下文的具体回调。</summary>
    internal static long BeginNative(MethodBase method)
    {
        if (!enabled) { return 0; }
        NativeCalls.TryGetValue(method, out var calls); NativeCalls[method] = ++calls;
        return (calls & 31) == 1 ? Stopwatch.GetTimestamp() : 0;
    }
    private static readonly Dictionary<MethodBase, (long Samples, long Ticks)> NativeTimes = new();
    internal static void EndNative(MethodBase? method, long time)
    {
        if (time == 0 || method == null) { return; }
        var elapsed = Stopwatch.GetTimestamp() - time;
        NativeTimes.TryGetValue(method, out var value); NativeTimes[method] = (value.Samples + 1, value.Ticks + elapsed);
    }
    private static long started;
    private static int firstFrame;
    private static bool enabled;

    /// <summary>在当前候选上采样适配、碰撞和装饰的实际主线程耗时。</summary>
    /// <param name="light">只计时帧级入口，跳过每帧上万次的小方法，降低采样本身的拖慢。</param>
    internal static void Start(bool light = false)
    {
        Stop(); Counters.Clear(); HitEvents.Clear(); NativeCalls.Clear(); NativeTimes.Clear();
        // 每层分别计时；父子方法的耗时不可相加。
        foreach (var type in new[] { typeof(Plugin), typeof(GameActions), typeof(NativeEnemyAlly), typeof(SummonFactionOutline), typeof(NativeAllyExecution), typeof(FriendlyFsmExecutionHook), typeof(DamageEnemies), typeof(FriendlyCollisionPairs), typeof(QaHarness), typeof(HutongGames.PlayMaker.Fsm), typeof(PlayMakerFSM), typeof(CompanionLocomotion), typeof(NativeAllyProjectile), typeof(CompanionFocus), typeof(QaHostileContact), typeof(NativeCombatContacts) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (method.Name is not ("OnGUI" or "Tick" or "Update" or "FixedUpdate" or "LateUpdate" or "Retarget" or "SteerToTarget" or "StrikeEnemies" or "TakeEnemyHits" or "Before" or "BeforeIterator" or "Prefix" or "OnTriggerEnter2D" or "OnCollisionEnter2D" or "DoDamage" or "EvaluateDamage" or "ProcessDamageBuffer" or "LateFixedUpdate" or "Refresh" or "Remove" or "BeforeSummonHit" or "AfterSummonHit" or "BeforeHeroDamage" or "BeforeHealthLoss" or "After" or "Prepare" or "InNativeRange" or "Release" or "Step" or "Register" or "AssignSpawn" or "IgnoreHero" or "FindHostile" or "For" or "Align" or "MaintainAll" or "TargetInRange" or "get_CanChangeTarget" or "get_TargetObject" or "get_HasEnemy" or "Enemies" or "DrainPending" or "TrimUnusedPrefabs" or "TickPickups" or "CaptureStatus")) { continue; }
                if (light && method.Name is "For" or "get_HasEnemy" or "get_TargetObject" or "get_CanChangeTarget" or "Before" or "BeforeIterator" or "After" or "TargetInRange" or "InNativeRange" or "IgnoreHero" or "Prefix" or "Refresh") { continue; }
                Hooks.Patch(method, prefix: new HarmonyMethod(typeof(QaRuntimeTimings), nameof(Before)),
                    finalizer: new HarmonyMethod(typeof(QaRuntimeTimings), nameof(After)));
            }
        }
        Hooks.Patch(AccessTools.Method(typeof(HealthManager), "Hit", new[] { typeof(HitInstance) }),
            prefix: new HarmonyMethod(typeof(QaRuntimeTimings), nameof(BeforeHit)),
            postfix: new HarmonyMethod(typeof(QaRuntimeTimings), nameof(AfterHit)));
        started = Stopwatch.GetTimestamp(); firstFrame = Time.frameCount; enabled = true;
    }

    /// <summary>移除本次所有观察补丁。</summary>
    internal static void Stop() { enabled = false; Hooks.UnpatchSelf(); }

    /// <summary>取得方法入口时间。</summary>
    private static void Before(out long __state) { __state = enabled ? Stopwatch.GetTimestamp() : 0; }

    /// <summary>记录实际耗时，异常不被吞掉。</summary>
    private static void After(MethodBase __originalMethod, long __state)
    {
        if (__state == 0) { return; }
        var elapsed = Stopwatch.GetTimestamp() - __state;
        Counters.TryGetValue(__originalMethod, out var previous);
        Counters[__originalMethod] = (previous.Calls + 1, previous.Ticks + elapsed, Math.Max(previous.Max, elapsed));
    }

    /// <summary>记录真正进入原作命中入口的来源和目标。</summary>
    private static void BeforeHit(HealthManager __instance, HitInstance __0, out int __state)
    {
        __state = __instance.hp;
    }

    /// <summary>只观察实际命中结果；不代替伤害。</summary>
    private static void AfterHit(HealthManager __instance, HitInstance __0, int __state)
    {
        if (HitEvents.Count >= 400) { return; }
        HitEvents.Add(new JObject { ["Frame"] = Time.frameCount, ["Target"] = __instance.name,
            ["TargetId"] = __instance.GetComponentInParent<OwnedSummon>()?.GetInstanceID(),
            ["Source"] = __0.Source != null ? __0.Source.name : "", ["Hero"] = __0.IsHeroDamage,
            ["Nail"] = __0.IsNailTag, ["FriendlyFlow"] = NativeAllyExecution.IsFriendlyFlow,
            ["Before"] = __state, ["After"] = __instance.hp });
    }

    /// <summary>返回样本及性能环境。</summary>
    internal static JObject Snapshot()
    {
        var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        return new JObject { ["Seconds"] = elapsed, ["Frames"] = Time.frameCount - firstFrame,
            ["Focused"] = Application.isFocused, ["Methods"] = new JArray(Counters.OrderByDescending(p => p.Value.Ticks).Select(p =>
                new JObject { ["Method"] = p.Key.DeclaringType!.Name + "." + p.Key.Name,
                    ["Calls"] = p.Value.Calls, ["TotalMs"] = p.Value.Ticks * 1000d / Stopwatch.Frequency,
                    ["MaxMs"] = p.Value.Max * 1000d / Stopwatch.Frequency })),
            ["NativeTimes"] = new JArray(NativeTimes.OrderByDescending(p => p.Value.Ticks * NativeCalls[p.Key] / (double)p.Value.Samples).Take(25).Select(p =>
                new JObject { ["Method"] = p.Key.DeclaringType!.Name + "." + p.Key.Name, ["Calls"] = NativeCalls[p.Key], ["Samples"] = p.Value.Samples,
                    ["EstimatedTotalMs"] = p.Value.Ticks * 1000d / Stopwatch.Frequency * NativeCalls[p.Key] / p.Value.Samples })),
            ["NativeCalls"] = new JArray(NativeCalls.OrderByDescending(p => p.Value).Take(25).Select(p =>
                new JObject { ["Method"] = p.Key.DeclaringType!.Name + "." + p.Key.Name, ["Calls"] = p.Value })),
            ["Hits"] = new JArray(HitEvents), ["Hero"] = HeroState() };
    }

    /// <summary>观察主角真实动画、朝向和针碰撞开关。</summary>
    internal static JObject HeroState()
    {
        var hero = HeroController.SilentInstance;
        return new JObject { ["Focused"] = Application.isFocused, ["FacingRight"] = hero.cState.facingRight,
            ["MoveInput"] = JToken.FromObject(AccessTools.Field(typeof(HeroController), "move_input").GetValue(hero)),
            ["Attacking"] = hero.cState.attacking, ["Clip"] = hero.GetComponent<tk2dSpriteAnimator>()?.CurrentClip?.name,
            ["DamageObjects"] = new JArray(hero.GetComponentsInChildren<DamageEnemies>(true)
                .Where(d => d.gameObject.activeInHierarchy).Select(d => new JObject { ["Name"] = d.name,
                    ["Enabled"] = d.enabled, ["Colliders"] = new JArray(d.GetComponents<Collider2D>().Select(c =>
                        new JObject { ["Enabled"] = c.enabled, ["X"] = c.bounds.center.x, ["Y"] = c.bounds.center.y,
                            ["W"] = c.bounds.size.x, ["H"] = c.bounds.size.y })) })) };
    }
}
#endif

