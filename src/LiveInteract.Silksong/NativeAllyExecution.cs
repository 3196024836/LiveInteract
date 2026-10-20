using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
namespace LiveInteract.Silksong;
/// <summary>用实际原生状态机/控制器的执行上下文标记投射物来源，不按距离猜测阵营。</summary>
internal static class NativeAllyExecution
{
    [ThreadStatic] public static NativeEnemyAlly? Current;
    [ThreadStatic] public static bool InSpawnedFlow;
    private static readonly Harmony hooks=new("cn.liveinteract.native-projectiles");
    private static readonly HashSet<MethodBase> patched=new();
    public static void Register(GameObject prefab)
    {
        foreach(var type in prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&c.GetType().Assembly==typeof(HealthManager).Assembly).Select(c=>c.GetType()).Distinct())
        foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
        {
            var iterator=method.GetCustomAttribute<IteratorStateMachineAttribute>();
            if(iterator!=null)
            {var move=AccessTools.Method(iterator.StateMachineType,"MoveNext");if(move!=null&&patched.Add(move))hooks.Patch(move,prefix:new HarmonyMethod(typeof(NativeAllyExecution),nameof(BeforeIterator)),finalizer:new HarmonyMethod(typeof(NativeAllyExecution),nameof(After)));}
            if(method.IsAbstract||method.IsGenericMethod||method.ReturnType!=typeof(void))continue;
            var name=method.Name.ToLowerInvariant();
            if(name is not ("awake" or "start" or "onenable" or "update" or "fixedupdate" or "lateupdate") && !new[]{"shoot","fire","throw","spawn","launch","attack","projectile"}.Any(name.Contains))continue;
            if(patched.Add(method))hooks.Patch(method,prefix:new HarmonyMethod(typeof(NativeAllyExecution),nameof(Before)),finalizer:new HarmonyMethod(typeof(NativeAllyExecution),nameof(After)));
        }
    }
    private static void Before(MonoBehaviour __instance,out NativeCallState __state)
    {__state=new NativeCallState{Ally=Current,Spawned=InSpawnedFlow};Current=__instance.GetComponentInParent<NativeEnemyAlly>();InSpawnedFlow=InSpawnedFlow||__instance.GetComponentInParent<OwnedSummon>()!=null;}
    private static void BeforeIterator(object __instance,out NativeCallState __state)
    {__state=new NativeCallState{Ally=Current,Spawned=InSpawnedFlow};var field=__instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).OrderBy(f=>f.Name=="<>4__this"?0:1).FirstOrDefault(f=>typeof(MonoBehaviour).IsAssignableFrom(f.FieldType));var owner=field?.GetValue(__instance) as MonoBehaviour;Current=owner?.GetComponentInParent<NativeEnemyAlly>();InSpawnedFlow=InSpawnedFlow||owner?.GetComponentInParent<OwnedSummon>()!=null;}
    private static void After(NativeCallState __state){Current=__state.Ally;InSpawnedFlow=__state.Spawned;}
    public static bool IsFriendly(GameObject? source)=>source!=null&&(source.GetComponentInParent<OwnedCompanion>()!=null||source.GetComponentsInChildren<NativeAllyProjectile>(true).Any(p=>p.WasFriendly)||source.GetComponentInParent<NativeAllyProjectile>()?.WasFriendly==true);
    /// <summary>来源属于主播或本工具友军；用于让友军只承受敌方伤害。</summary>
    public static bool IsHeroOwned(GameObject? source)=>source!=null&&(source.GetComponentInParent<HeroController>()!=null||IsFriendly(source));
    /// <summary>命中来自主播或友军本体时，友军不结算伤害；AttackTypes.Nail 是枚举零值，不能用来判定来源。</summary>
    public static bool IsHeroHit(HitInstance hit)=>hit.IsHeroDamage||hit.IsNailTag||IsHeroOwned(hit.Source);
    public static void Dispose(){hooks.UnpatchSelf();patched.Clear();Current=null;InSpawnedFlow=false;}
}
/// <summary>原生状态机执行时保留真实召唤来源；不再用这个入口封锁全局存档写入，避免影响长椅、重生等原作流程。</summary>
[HarmonyPatch]
internal static class FriendlyFsmExecutionHook
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(Fsm).GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.DeclaringType==typeof(Fsm)&&(m.Name is "Update" or "FixedUpdate" or "LateUpdate" or "Event"));
    private static void Prefix(Fsm __instance,out NativeEnemyAlly? __state){__state=NativeAllyExecution.Current;if(__state==null)NativeAllyExecution.Current=__instance.GameObject?.GetComponentInParent<NativeEnemyAlly>();}
    private static void Finalizer(NativeEnemyAlly? __state){NativeAllyExecution.Current=__state;}
}
/// <summary>池化原生投射物每次启用重新归属；离开友军来源时清除旧标记，避免把普通敌人子弹误当友军。</summary>
internal sealed class NativeAllyProjectile : MonoBehaviour
{
    public NativeEnemyAlly? Owner;
    public bool WasFriendly;
    public void Assign(NativeEnemyAlly? owner,DamageHero weapon)
    {if(Owner!=null)Owner.RemoveProjectile(weapon);Owner=owner;WasFriendly=owner!=null;if(owner!=null)owner.AddProjectile(weapon,this);}
    public void Disarm(){Owner=null;WasFriendly=true;}
}
[HarmonyPatch(typeof(DamageHero),"OnEnable")]
internal static class FriendlyProjectileOriginHook
{
    private static void Prefix(DamageHero __instance)
    {
        if(__instance.GetComponentInParent<NativeEnemyAlly>()!=null)return;
        var marker=__instance.GetComponent<NativeAllyProjectile>();var owner=NativeAllyExecution.Current;
        if(marker==null&&owner!=null)marker=__instance.gameObject.AddComponent<NativeAllyProjectile>();
        if(marker!=null)marker.Assign(owner,__instance);
    }
}

internal struct NativeCallState { public NativeEnemyAlly? Ally; public bool Spawned; }
/// <summary>召唤物执行流不能写入全局剧情/解锁变量；原作拾取物在真实拾取时的入账不在此执行流内。</summary>
[HarmonyPatch]
internal static class SummonStoryWriteHook
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(PlayerData).GetMethods(BindingFlags.Instance|BindingFlags.Public).Where(m=>m.Name is "SetBool" or "SetInt" or "SetFloat" or "SetString");
    private static bool Prefix(string __0)=>!NativeAllyExecution.InSpawnedFlow || new[]{"health","healthBlue","silk","geo","shellShards"}.Contains(__0,StringComparer.OrdinalIgnoreCase);
}

/// <summary>原生投掷/延迟攻击跨帧时继续保留真实召唤来源；不把附近敌人投射物猜成友军。</summary>
[HarmonyPatch(typeof(MonoBehaviour),nameof(MonoBehaviour.StartCoroutine),new[]{typeof(IEnumerator)})]
internal static class FriendlyCoroutineOriginHook
{
    private static void Prefix(MonoBehaviour __instance,ref IEnumerator __0)
    {
        if(__0==null||__0 is SummonScopedRoutine)return;
        var ally=NativeAllyExecution.Current ?? __instance.GetComponentInParent<NativeEnemyAlly>();
        if(ally!=null)__0=new SummonScopedRoutine(__0,ally);
    }
}
internal sealed class SummonScopedRoutine : IEnumerator,IDisposable
{
    private readonly IEnumerator inner;
    private readonly NativeEnemyAlly? ally;
    private readonly bool hadFriendly;
    public SummonScopedRoutine(IEnumerator inner,NativeEnemyAlly? ally){this.inner=inner;this.ally=ally;hadFriendly=ally!=null;}
    public object? Current=>inner.Current is IEnumerator nested ? new SummonScopedRoutine(nested,ally) : inner.Current;
    public bool MoveNext()
    {
        if(hadFriendly&&ally==null)return false;
        var previous=new NativeCallState{Ally=NativeAllyExecution.Current,Spawned=NativeAllyExecution.InSpawnedFlow};
        NativeAllyExecution.Current=ally;
        try{return inner.MoveNext();}finally{NativeAllyExecution.Current=previous.Ally;NativeAllyExecution.InSpawnedFlow=previous.Spawned;}
    }
    public void Reset()=>inner.Reset();
    public void Dispose(){if(inner is IDisposable disposable)disposable.Dispose();}
}
