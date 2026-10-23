using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LiveInteract.Contracts;
using HutongGames.PlayMaker;
using UnityEngine;
namespace LiveInteract.Silksong;

/// <summary>在原作 AI、FSM、延迟攻击和对象池之间传递实际来源，不按距离猜阵营。</summary>
internal static class NativeAllyExecution
{
    [ThreadStatic] public static NativeEnemyAlly? Current;
    [ThreadStatic] public static GameObject? Origin;
    [ThreadStatic] public static GameObject? FallbackTarget;
    [ThreadStatic] public static bool InSpawnedFlow;
    [ThreadStatic] public static bool IsFriendlyFlow;
    [ThreadStatic] public static string? ResourceKey;
    private static readonly Harmony hooks=new("cn.liveinteract.native-projectiles");
    private static readonly HashSet<MethodBase> patched=new();
    private static readonly HashSet<string> missingProtection=new();
    public static void ProtectionFailed(string name){missingProtection.Add(name);}
    public static void EnsureFriendlyProtection(){if(missingProtection.Count>0)throw new DeferredActionException("FRIENDLY_GUARD_UNAVAILABLE","友军保护补丁未完整加载，拒绝生成以免误伤："+string.Join("、",missingProtection));}
    private static readonly HashSet<string> missingStoryProtection=new();
    public static void StoryProtectionFailed(string name)=>missingStoryProtection.Add(name);
    public static void EnsureStoryProtection()
    {
        if(missingStoryProtection.Count>0)throw new DeferredActionException("SUMMON_STORY_GUARD_UNAVAILABLE",
            "召唤剧情隔离未完整加载，暂不生成以免触发结局："+string.Join("、",missingStoryProtection));
    }
    public static int StoryBlockedCount{get;private set;}
    public static void NotifyStoryBlocked(string route)
    {
        StoryBlockedCount++;GameActions.Current?.ReportSummonStoryBlocked(route,ResourceKey);
    }
    public static NativeCallState Capture()=>new(){Ally=Current,Origin=Origin,FallbackTarget=FallbackTarget,Spawned=InSpawnedFlow,Friendly=IsFriendlyFlow,ResourceKey=ResourceKey};
    public static void Restore(NativeCallState state){Current=state.Ally;Origin=state.Origin;FallbackTarget=state.FallbackTarget;InSpawnedFlow=state.Spawned;IsFriendlyFlow=state.Friendly;ResourceKey=state.ResourceKey;}
    public static void Register(GameObject prefab)
    {
        foreach(var type in prefab.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&c.GetType().Assembly==typeof(HealthManager).Assembly).Select(c=>c.GetType()).Distinct())
        foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
        {
            if(method.IsAbstract||method.IsGenericMethod||method.GetMethodBody()==null||!patched.Add(method))continue;
            // 纯字段读写没有回调、派生物或坐标读取；无需为每次属性访问重复进入阵营上下文。
            if(!PatchProcessor.ReadMethodBody(method).Any(instruction=>
                instruction.Key==OpCodes.Call || instruction.Key==OpCodes.Callvirt ||
                instruction.Key==OpCodes.Calli || instruction.Key==OpCodes.Newobj || instruction.Key==OpCodes.Jmp))continue;
            hooks.Patch(method,prefix:new HarmonyMethod(typeof(NativeAllyExecution),method.Name is "Update" or "FixedUpdate" or "LateUpdate"?nameof(BeforeTick):nameof(Before)),finalizer:new HarmonyMethod(typeof(NativeAllyExecution),nameof(After)),transpiler:new HarmonyMethod(typeof(NativeAllyExecution),nameof(RetargetInstructions)));
            var iterator=method.GetCustomAttribute<IteratorStateMachineAttribute>();
            var move=iterator==null?null:AccessTools.Method(iterator.StateMachineType,"MoveNext");
            if(move!=null&&patched.Add(move))hooks.Patch(move,prefix:new HarmonyMethod(typeof(NativeAllyExecution),nameof(BeforeIterator)),finalizer:new HarmonyMethod(typeof(NativeAllyExecution),nameof(After)),transpiler:new HarmonyMethod(typeof(NativeAllyExecution),nameof(RetargetInstructions)));
        }
    }
    /// <summary>原作方法只进入来源上下文，不逐次获取反射方法对象。</summary>
    private static bool Before(MonoBehaviour __instance,out NativeCallState __state)
    {
        __state=Capture();if(__instance==null)return true;
        // 同一本体内部的嵌套调用沿用已确认的来源；不跨本体继承。
        if(Current!=null && Origin==Current.gameObject && __instance.gameObject==Origin)return true;
        Enter(__instance);return true;
    }
    /// <summary>只在注册生命周期回调时附加暂停门禁；清理和其他事件仍能正常执行。</summary>
    private static bool BeforeTick(MonoBehaviour __instance,out NativeCallState __state)
    {
        Before(__instance,out __state);
        return GameActions.Current?.CanAct==true || __instance==null || __instance.GetComponentInParent<OwnedSummon>()==null;
    }
    private static void Enter(MonoBehaviour __instance)
    {
        var owned=__instance.GetComponentInParent<OwnedSummon>();
        var projectile=__instance.GetComponentInParent<NativeAllyProjectile>();
        if(owned!=null){Current=owned.GetComponent<NativeEnemyAlly>();Origin=owned.gameObject;FallbackTarget=owned.gameObject;InSpawnedFlow=true;IsFriendlyFlow=owned.GetComponent<OwnedCompanion>()!=null;ResourceKey=owned.AssetKey;}
        else if(TryEnterTransient(__instance.gameObject)) { }
        else if(projectile!=null&&projectile.HasOrigin){Current=projectile.Owner;Origin=projectile.Origin;FallbackTarget=projectile.gameObject;InSpawnedFlow=projectile.WasFriendly;IsFriendlyFlow=projectile.WasFriendly;ResourceKey=projectile.GetComponent<OwnedTransientResource>()?.AssetKey;}
        else if(!IsFriendlyFlow||__instance is HealthManager){Current=null;Origin=__instance.gameObject;FallbackTarget=null;IsFriendlyFlow=false;ResourceKey=null;}
        // 新弹体的 OnEnable 没有父级，必须继承调用者；不能把仍有效的友方上下文清空。
    }
    private static void BeforeIterator(object __instance,out NativeCallState __state)
    {
        __state=Capture();
        var field=__instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).OrderBy(f=>f.Name=="<>4__this"?0:1).FirstOrDefault(f=>typeof(MonoBehaviour).IsAssignableFrom(f.FieldType));
        if(field?.GetValue(__instance) is MonoBehaviour owner)Enter(owner);
    }
    private static void After(NativeCallState __state)
    {
        Restore(__state);
    }
    /// <summary>尸体/特效不是友军实体，但后续原生回调仍必须保留“来自召唤物”及原阵营。</summary>
    public static bool TryEnterTransient(GameObject obj)
    {
        var resource=obj.GetComponentInParent<OwnedTransientResource>();
        if(resource==null||string.IsNullOrEmpty(resource.AssetKey))return false;
        Current=obj.GetComponentInParent<NativeAllyProjectile>()?.Owner;
        Origin=resource.gameObject;FallbackTarget=resource.gameObject;InSpawnedFlow=true;
        IsFriendlyFlow=resource.Friendly;ResourceKey=resource.AssetKey;return true;
    }
    /// <summary>友军本体、脱离父级的派生物及延迟资源共享阵营，不因缺少OwnedCompanion误变成敌人。</summary>
    /// <param name="source">待识别的原作物体。</param>
    /// <returns>是否属于友军召唤链。</returns>
    public static bool IsFriendly(GameObject? source)=>source!=null&&CompanionCombatPolicy.IsFriendlyEntity(
        source.GetComponentInParent<OwnedCompanion>()!=null,
        source.GetComponentInParent<NativeAllyProjectile>()?.WasFriendly==true,
        source.GetComponentInParent<OwnedTransientResource>()?.Friendly==true);
    public static bool IsHeroOwned(GameObject? source)=>source!=null&&(source.GetComponentInParent<HeroController>()!=null||IsFriendly(source));
    public static bool ProtectFriendlyHit(HealthManager target,HitInstance hit)
    {
        if(!IsFriendly(target.gameObject))return false;
        var companion=target.GetComponentInParent<OwnedCompanion>();
        if(IsHeroHit(hit))return true;
        var hero=HeroController.SilentInstance;
        if(hero!=null&&hero.cState.transitioning)return true;
        // 原作水面会给Enemy层9999伤害；随行小弟在传送入口不能因此被误清理。
        if(hit.Source!=null&&hit.Source.GetComponentInParent<SurfaceWaterRegion>()!=null)
        {companion?.GetComponent<NativeEnemyAlly>()?.SnapToHero();return true;}
        return false;
    }
    public static bool IsHeroHit(HitInstance hit)=>hit.IsHeroDamage||hit.IsNailTag||IsHeroOwned(hit.Source);
    /// <summary>发射者死亡后，弹体仍保留友方身份，失去目标时不回退成追踪主播。</summary>
    internal static Transform TargetTransform(Component component)=>TargetGameObject(component).transform;
    internal static GameObject TargetGameObject(Component component)
    {
        if(component is not HeroController||!IsFriendlyFlow||Current?.NativeFriendlyAi==true)return component.gameObject;
        var target=Current!=null?Current.TargetObject:null;
        if(target!=null)return target;
        if(FallbackTarget!=null)return FallbackTarget;
        if(Origin!=null)return Origin;
        throw new InvalidOperationException("友方执行缺少独立目标，拒绝回退到主播");
    }
    internal static IEnumerable<CodeInstruction> RetargetInstructions(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo method && method.DeclaringType==typeof(Component))
            {
                if(method.Name=="get_transform"){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(NativeAllyExecution),nameof(TargetTransform));}
                else if(method.Name=="get_gameObject"){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(NativeAllyExecution),nameof(TargetGameObject));}
            }
            // 原作DeparentAndFollow使用transform.parent=null；与SetParent共享脱离前归属保存。
            if(instruction.operand is MethodInfo parentMethod && parentMethod.DeclaringType==typeof(Transform) &&
                (parentMethod.Name is nameof(Transform.SetParent) or "set_parent"))
            {
                var parameters=parentMethod.GetParameters();
                var signature=parameters.Length==1?new[]{typeof(Transform),typeof(Transform)}:new[]{typeof(Transform),typeof(Transform),typeof(bool)};
                instruction.opcode=OpCodes.Call;
                instruction.operand=typeof(NativeAllyExecution).GetMethod(nameof(SetNativeParent),BindingFlags.Static|BindingFlags.NonPublic,null,signature,null)!;
            }
            yield return instruction;
        }
    }
    // Only registered original actor/corpse methods are rewritten, not Unity's global Transform API.
    internal static void SetNativeParent(Transform child,Transform parent)
    { PreserveDetachedOrigin(child,parent);child.SetParent(parent); }
    internal static void SetNativeParent(Transform child,Transform parent,bool worldPositionStays)
    { PreserveDetachedOrigin(child,parent);child.SetParent(parent,worldPositionStays); }
    private static void PreserveDetachedOrigin(Transform child,Transform parent)
    {
        if(child==null)return;
        var owner=child.GetComponentInParent<OwnedSummon>();
        var source=child.GetComponentInParent<OwnedTransientResource>();
        var key=owner!=null?owner.AssetKey:source?.AssetKey;
#if LIVEINTERACT_QA
        if(!string.IsNullOrEmpty(key)||InSpawnedFlow)QaHarness.RecordActor(child.gameObject,"native-detach:"+(key??"unowned")+":"+child.parent?.name);
#endif
        if(string.IsNullOrEmpty(key)||owner!=null&&child==owner.transform)return;
        var origin=owner!=null?owner.transform:source!.transform;
        if(parent!=null&&(parent==origin||parent.IsChildOf(origin)))return;
        var resource=child.GetComponent<OwnedTransientResource>()??child.gameObject.AddComponent<OwnedTransientResource>();
        resource.AssetKey=key!;resource.Friendly=owner!=null?owner.GetComponent<OwnedCompanion>()!=null:source!.Friendly;
        if(owner!=null&&resource.DetachedOwner==null){resource.DetachedOwner=owner;owner.DetachedResources.Add(resource);}
    }
    public static void AssignSpawn(GameObject instance)
    {
        var resource=instance.GetComponent<OwnedTransientResource>();
        if(InSpawnedFlow&&!string.IsNullOrEmpty(ResourceKey))
        {
            resource??=instance.AddComponent<OwnedTransientResource>();resource.AssetKey=ResourceKey!;
            resource.Friendly=IsFriendlyFlow||Current!=null;resource.DetachedOwner=null;Register(instance);
        }
        else if(resource!=null){resource.AssetKey="";resource.Friendly=false;resource.DetachedOwner=null;}
        var marker=instance.GetComponent<NativeAllyProjectile>();
        if(IsFriendlyFlow||Current!=null)
        {
            var harmful=instance.GetComponentsInChildren<DamageHero>(true).Length>0||instance.GetComponentsInChildren<HealthManager>(true).Length>0;
            if(!harmful&&marker==null)return;
            marker??=instance.AddComponent<NativeAllyProjectile>();marker.Assign(Current,Origin,true);
        }
        else if(marker!=null)marker.Assign(null,Origin,false);
    }
    public static void Dispose(){hooks.UnpatchSelf();patched.Clear();Current=null;Origin=null;FallbackTarget=null;InSpawnedFlow=false;IsFriendlyFlow=false;}
}
internal struct NativeCallState
{
    public NativeEnemyAlly? Ally; public GameObject? Origin; public GameObject? FallbackTarget; public bool Spawned; public bool Friendly; public string? ResourceKey;
}

[HarmonyPatch]
internal static class FriendlyFsmExecutionHook
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(Fsm).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(m=>m.DeclaringType==typeof(Fsm)&&(m.Name is "Event" or "SetState" or "EnterState" or "SwitchState" or "ReEnterState" or "OnEnable" or "Start"));
    /// <summary>事件和生命周期共用同一个来源解析，避免分别维护阵营规则。</summary>
    internal static OwnedSummon? Enter(Fsm instance,out NativeCallState state)
    {
        state=NativeAllyExecution.Capture();var obj=instance.GameObject;if(obj==null)return null;
        var owned=obj.GetComponentInParent<OwnedSummon>();var projectile=obj.GetComponentInParent<NativeAllyProjectile>();
        if(owned!=null){NativeAllyExecution.Current=owned.GetComponent<NativeEnemyAlly>();NativeAllyExecution.Origin=owned.gameObject;NativeAllyExecution.FallbackTarget=owned.gameObject;NativeAllyExecution.InSpawnedFlow=true;NativeAllyExecution.IsFriendlyFlow=owned.GetComponent<OwnedCompanion>()!=null;NativeAllyExecution.ResourceKey=owned.AssetKey;}
        else if(NativeAllyExecution.TryEnterTransient(obj)) { }
        else if(projectile!=null&&projectile.HasOrigin){NativeAllyExecution.Current=projectile.Owner;NativeAllyExecution.Origin=projectile.Origin;NativeAllyExecution.FallbackTarget=projectile.gameObject;NativeAllyExecution.InSpawnedFlow=projectile.WasFriendly;NativeAllyExecution.IsFriendlyFlow=projectile.WasFriendly;NativeAllyExecution.ResourceKey=projectile.GetComponent<OwnedTransientResource>()?.AssetKey;}
        return owned;
    }
    private static void Prefix(Fsm __instance,out NativeCallState __state)=>Enter(__instance,out __state);
    private static void Finalizer(NativeCallState __state)=>NativeAllyExecution.Restore(__state);
}
/// <summary>只对固定的三个FSM更新入口执行随行暂停和游戏状态门禁。</summary>
[HarmonyPatch]
internal static class FriendlyFsmTickHook
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(Fsm).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(m=>m.DeclaringType==typeof(Fsm)&&(m.Name is "Update" or "FixedUpdate" or "LateUpdate"));
    private static bool Prefix(Fsm __instance,out NativeCallState __state)
    {
        var owned=FriendlyFsmExecutionHook.Enter(__instance,out __state);
        if(owned!=null&&GameActions.Current?.CanAct!=true)return false;
        return owned?.GetComponent<NativeEnemyAlly>()?.SuspendIdleFsm(__instance)!=true;
    }
    private static void Finalizer(NativeCallState __state)=>NativeAllyExecution.Restore(__state);
}

/// <summary>保留原作弹体开关与碰撞时机；池化交回敌方时恢复原阵营，不留下永久免伤标记。</summary>
internal sealed class NativeAllyProjectile : MonoBehaviour
{
    public NativeEnemyAlly? Owner;
    public GameObject? Origin;
    public bool WasFriendly;
    public bool HasOrigin;
    private readonly List<(Collider2D Own,Collider2D Hero,bool WasIgnored)> pairs=new();
    // 碰撞对按实例标识查重，避免每半秒对全部碰撞对进行平方级Unity对象比较。
    private readonly HashSet<(int Own, int Hero)> heroPairIds = new();
    private readonly List<DamageHero> weapons=new();
    public void Assign(NativeEnemyAlly? owner,GameObject? origin,bool friendly)
    {
        if(Owner!=null)foreach(var weapon in weapons)if(weapon!=null)Owner.RemoveProjectile(weapon);
        WasFriendly=false;FriendlyCollisionPairs.Remove(this);
        RestoreCollisions();weapons.Clear();Owner=owner;Origin=origin;WasFriendly=friendly;HasOrigin=true;
        if(!friendly)return;
        foreach(var weapon in GetComponentsInChildren<DamageHero>(true))
        {weapons.Add(weapon);if(owner!=null)owner.AddProjectile(weapon,this);}
        IgnoreHero();FriendlyCollisionPairs.Refresh(this);
    }
    private void OnEnable()
    {
        if (!WasFriendly) { return; }
        IgnoreHero();
        FriendlyCollisionPairs.Refresh(this);
    }
    public void IgnoreHero()
    {
        var hero=HeroController.SilentInstance;if(hero==null)return;
        // 每次刷新仅读取一次主播层级；仍重施被原作启停清除的忽略关系。
        var heroColliders = hero.GetComponentsInChildren<Collider2D>(true);
        foreach(var own in GetComponentsInChildren<Collider2D>(true))
        foreach(var collider in heroColliders)
        {
            // 初次记录原值，之后只重应用：原作会停用攻击框和HeroBox，不能因缓存存在就漏掉恢复。
            if (heroPairIds.Add((own.GetInstanceID(), collider.GetInstanceID())))
            {
                pairs.Add((own, collider, Physics2D.GetIgnoreCollision(own, collider)));
            }
            if (own.enabled && collider.enabled && own.gameObject.activeInHierarchy && collider.gameObject.activeInHierarchy &&
                !Physics2D.GetIgnoreCollision(own, collider))
            {
                Physics2D.IgnoreCollision(own, collider, true);
            }
        }
    }
    private void RestoreCollisions(){foreach(var pair in pairs)if(pair.Own!=null&&pair.Hero!=null)Physics2D.IgnoreCollision(pair.Own,pair.Hero,pair.WasIgnored);pairs.Clear();heroPairIds.Clear();}
    public void Disarm(){Owner=null;WasFriendly=true;}
    private void OnDestroy(){WasFriendly=false;FriendlyCollisionPairs.Remove(this);RestoreCollisions();}
}

[HarmonyPatch(typeof(ObjectPool),"ActivateSpawningObject")]
internal static class FriendlyPoolSpawnHook
{
    private static void Prefix(GameObject __0)=>NativeAllyExecution.AssignSpawn(__0);
}
/// <summary>原作会把预置地刺从本体移出；移出前保存来源，后续攻击和 F8 清理不依赖旧父级。</summary>
[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.SetParent), "OnEnter")]
internal static class FriendlyDetachedChildHook
{
    private static void Prefix(HutongGames.PlayMaker.Actions.SetParent __instance)
    {
        var child = __instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
        if (child == null) { return; }
        var owned = child.GetComponentInParent<OwnedSummon>();
        if (owned == null || child == owned.gameObject) { return; }
        var parent = __instance.parent.Value;
        if (parent != null && parent.GetComponentInParent<OwnedSummon>() == owned) { return; }

        // 只接管召唤副本已有的子物体，不收编场景对象或其他单位。
        var resource = child.GetComponent<OwnedTransientResource>() ?? child.AddComponent<OwnedTransientResource>();
        resource.AssetKey = owned.AssetKey;
        resource.DetachedOwner = owned;
        owned.DetachedResources.Add(resource);
        resource.Friendly = owned.GetComponent<OwnedCompanion>() != null;
        if (!resource.Friendly) { return; }
        var ally = owned.GetComponent<NativeEnemyAlly>();
        var marker = child.GetComponent<NativeAllyProjectile>() ?? child.AddComponent<NativeAllyProjectile>();
        marker.Assign(ally, owned.gameObject, true);
    }
}

/// <summary>召唤根节点脱离原父级后，不再钳回来源竞技场；子部件与原场景单位约束保持不变。</summary>
[HarmonyPatch(typeof(ConstrainPosition), "LateUpdate")]
internal static class FriendlySummonSceneConstraintHook
{
    private static bool Prefix(ConstrainPosition __instance)
        => CompanionFollowPolicy.CanApplySourcePositionConstraint(__instance.GetComponent<OwnedSummon>() != null, __instance.localSpace);
}

[HarmonyPatch(typeof(DamageHero),"OnEnable")]
internal static class FriendlyProjectileOriginHook
{
    private static void Prefix(DamageHero __instance)
    {
        var ally=__instance.GetComponentInParent<NativeEnemyAlly>()??NativeAllyExecution.Current;
        if(ally==null&&!NativeAllyExecution.IsFriendlyFlow)return;
        var marker=__instance.GetComponentInParent<NativeAllyProjectile>();
        if(marker==null){marker=__instance.gameObject.AddComponent<NativeAllyProjectile>();marker.Assign(ally,NativeAllyExecution.Origin??ally?.gameObject,true);}
        else if(ally!=null){ally.AddProjectile(__instance,marker);marker.IgnoreHero();FriendlyCollisionPairs.Refresh(marker);}
    }
}
/// <summary>HeroBox 的缓冲判定也不得收录友军；不是给主播开启全局无敌。</summary>
[HarmonyPatch]
internal static class FriendlyHeroBoxHook
{
    private static IEnumerable<MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(HeroBox),nameof(HeroBox.CheckForDamage));}
    private static bool Prefix(GameObject __0)=>!NativeAllyExecution.IsFriendly(__0);
}
[HarmonyPatch]
internal static class FriendlyCanCauseDamageHook
{
    private static MethodBase TargetMethod()=>AccessTools.PropertyGetter(typeof(DamageHero),"CanCauseDamage");
    private static bool Prefix(DamageHero __instance,ref bool __result){if(!NativeAllyExecution.IsFriendly(__instance.gameObject))return true;__result=false;return false;}
}
/// <summary>只改写原作敌怪行动读取的角色坐标，友方执行时读敌人，敌方与主播执行时保持原样。</summary>
[HarmonyPatch]
internal static class FriendlyFsmTargetReadHook
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(var type in typeof(HealthManager).Assembly.GetTypes().Where(t=>!t.ContainsGenericParameters&&typeof(FsmStateAction).IsAssignableFrom(t)))
        foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly).Where(m=>!m.IsAbstract&&!m.ContainsGenericParameters&&m.GetMethodBody()!=null))
        {
            var bytes=method.GetMethodBody()!.GetILAsByteArray();var readsHero=false;
            for(var index=0;index+4<bytes.Length;index++)
            {
                if(bytes[index]!=0x28&&bytes[index]!=0x6f)continue;
                try{var called=method.Module.ResolveMethod(BitConverter.ToInt32(bytes,index+1));if(called?.DeclaringType==typeof(HeroController)){readsHero=true;break;}}
                catch(ArgumentException){}catch(BadImageFormatException){}
            }
            if(readsHero)yield return method;
        }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>NativeAllyExecution.RetargetInstructions(instructions);
}
[HarmonyPatch]
internal static class SummonStoryWriteHook
{
    private static IEnumerable<MethodBase> TargetMethods()=>typeof(PlayerData).GetMethods(BindingFlags.Instance|BindingFlags.Public).Where(m=>m.Name is "SetBool" or "SetInt" or "SetFloat" or "SetString");
    private static bool Prefix(string __0)
    {
        if(NativeAllyExecution.IsFriendlyFlow&&__0 is "health" or "healthBlue" or "silk")return false;
        return !NativeAllyExecution.InSpawnedFlow||new[]{"health","healthBlue","silk","geo","shellShards"}.Contains(__0,StringComparer.OrdinalIgnoreCase);
    }
}
[HarmonyPatch(typeof(MonoBehaviour),nameof(MonoBehaviour.StartCoroutine),new[]{typeof(IEnumerator)})]
internal static class FriendlyCoroutineOriginHook
{
    private static bool Prefix(MonoBehaviour __instance,ref IEnumerator __0,ref Coroutine __result)
    {
        // Unity不会在停用对象上启动协程；回收中的本工具弹体不要反复向原作发送无效请求。
        if(!__instance.gameObject.activeInHierarchy&&(__instance.GetComponentInParent<OwnedSummon>()!=null||
            __instance.GetComponentInParent<NativeAllyProjectile>()?.HasOrigin==true||
            !string.IsNullOrEmpty(__instance.GetComponentInParent<OwnedTransientResource>()?.AssetKey)))
        {__result=null!;return false;}
        if(__0==null||__0 is SummonScopedRoutine)return true;
        var ally=NativeAllyExecution.Current??__instance.GetComponentInParent<NativeEnemyAlly>();
        if(ally!=null||NativeAllyExecution.InSpawnedFlow)__0=new SummonScopedRoutine(__0,new NativeCallState{Ally=ally,Origin=NativeAllyExecution.Origin??ally?.gameObject??__instance.gameObject,FallbackTarget=__instance.gameObject,Spawned=true,Friendly=NativeAllyExecution.IsFriendlyFlow||ally!=null,ResourceKey=NativeAllyExecution.ResourceKey},__instance.GetComponentInParent<OwnedSummon>()!=null||__instance.GetComponentInParent<NativeAllyProjectile>()!=null||!string.IsNullOrEmpty(__instance.GetComponentInParent<OwnedTransientResource>()?.AssetKey),__instance.gameObject);
        return true;
    }
}
internal sealed class SummonScopedRoutine : IEnumerator,IDisposable
{
    private readonly IEnumerator inner;private readonly NativeCallState context;private readonly bool ownsLifetime;private readonly GameObject lifetimeOwner;
    public SummonScopedRoutine(IEnumerator inner,NativeCallState context,bool ownsLifetime,GameObject lifetimeOwner){this.inner=inner;this.context=context;this.ownsLifetime=ownsLifetime;this.lifetimeOwner=lifetimeOwner;}
    public object? Current=>ownsLifetime&&GameActions.Current?.CanAct!=true?null:inner.Current is IEnumerator nested?new SummonScopedRoutine(nested,context,ownsLifetime,lifetimeOwner):inner.Current;
    public bool MoveNext(){if(ownsLifetime&&lifetimeOwner==null)return false;if(ownsLifetime&&GameActions.Current?.CanAct!=true)return true;var previous=NativeAllyExecution.Capture();NativeAllyExecution.Restore(context);try{return inner.MoveNext();}finally{NativeAllyExecution.Restore(previous);}}
    public void Reset()=>inner.Reset();
    public void Dispose(){var previous=NativeAllyExecution.Capture();NativeAllyExecution.Restore(context);try{if(inner is IDisposable disposable)disposable.Dispose();}finally{NativeAllyExecution.Restore(previous);}}
}

/// <summary>原作在攻击/反击中临时重写目标时立即转向敌人，不能等下一帧才覆盖；不改全局变量。</summary>
[HarmonyPatch]
internal static class FriendlyFsmTargetWriteGuard
{
    private static MethodBase TargetMethod()=>AccessTools.PropertySetter(typeof(FsmGameObject),nameof(FsmGameObject.Value));
    private static void Prefix(FsmGameObject __instance,ref GameObject __0)
    {
        var ally=NativeAllyExecution.Current;
        if(ally!=null&&NativeAllyExecution.IsFriendlyFlow&&__0!=null&&__0.GetComponentInParent<HeroController>()!=null&&
            ally.OwnsTargetVariable(__instance))__0=ally.TargetObject!;
    }
}
/// <summary>友方执行中读取全局Hero目标也只得到其敌方/独立回退目标，原全局变量保持不变。</summary>
[HarmonyPatch]
internal static class FriendlyFsmTargetReadGuard
{
    private static MethodBase TargetMethod()=>AccessTools.PropertyGetter(typeof(FsmGameObject),nameof(FsmGameObject.Value));
    private static void Postfix(ref GameObject __result)
    {
        if(!NativeAllyExecution.IsFriendlyFlow||__result==null||__result.GetComponentInParent<HeroController>()==null)return;
        __result=NativeAllyExecution.Current?.TargetObject??NativeAllyExecution.FallbackTarget??NativeAllyExecution.Origin!;
    }
}
