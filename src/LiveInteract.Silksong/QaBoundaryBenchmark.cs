#if LIVEINTERACT_QA
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>只测上下文包装本身的成本，不运行原作招式、状态机或伤害。</summary>
internal sealed class QaBoundaryBenchmark : MonoBehaviour
{
    private static readonly Action<MonoBehaviour> Enter=(Action<MonoBehaviour>)Delegate.CreateDelegate(typeof(Action<MonoBehaviour>),AccessTools.Method(typeof(NativeAllyExecution),"Enter"));
    [MethodImpl(MethodImplOptions.NoInlining)] public int Baseline()=>1;
    [MethodImpl(MethodImplOptions.NoInlining)] public int WithMethod()=>1;
    [MethodImpl(MethodImplOptions.NoInlining)] public int WithoutMethod()=>1;

    private static bool BeforeWithMethod(MonoBehaviour __instance,MethodBase __originalMethod,out NativeCallState __state)
    {
        __state=NativeAllyExecution.Capture();if(__instance==null)return true;
        if(NativeAllyExecution.Current!=null&&NativeAllyExecution.Origin==NativeAllyExecution.Current.gameObject&&__instance.gameObject==NativeAllyExecution.Origin)
            return !(GameActions.Current?.CanAct!=true&&__originalMethod.Name is "Update" or "FixedUpdate" or "LateUpdate");
        Enter(__instance);
        return !(__instance.GetComponentInParent<OwnedSummon>()!=null&&GameActions.Current?.CanAct!=true&&__originalMethod.Name is "Update" or "FixedUpdate" or "LateUpdate");
    }
    /// <summary>保持同样的来源解析，但不要求Harmony逐次提供原方法反射对象。</summary>
    private static void BeforeNoMethod(MonoBehaviour __instance,out NativeCallState __state)
    {
        __state=NativeAllyExecution.Capture();
        if(__instance==null)return;
        if(NativeAllyExecution.Current!=null&&NativeAllyExecution.Origin==NativeAllyExecution.Current.gameObject&&__instance.gameObject==NativeAllyExecution.Origin)return;
        Enter(__instance);
    }

    /// <summary>在已有召唤物上测试，完成后移除补丁及临时组件并恢复上下文。</summary>
    internal static JObject Run()
    {
        if(GameActions.Current?.CanAct!=true)throw new InvalidOperationException("Game unavailable");
        var root=UnityEngine.Object.FindFirstObjectByType<NativeEnemyAlly>();
        if(root==null)throw new InvalidOperationException("Need a friendly actor");
        var probe=root.gameObject.AddComponent<QaBoundaryBenchmark>();
        var hooks=new Harmony("cn.liveinteract.qa.boundary-benchmark");var saved=NativeAllyExecution.Capture();
        try
        {
            hooks.Patch(AccessTools.Method(typeof(QaBoundaryBenchmark),nameof(WithMethod)),
                prefix:new HarmonyMethod(typeof(QaBoundaryBenchmark),nameof(BeforeWithMethod)),
                finalizer:new HarmonyMethod(AccessTools.Method(typeof(NativeAllyExecution),"After")));
            hooks.Patch(AccessTools.Method(typeof(QaBoundaryBenchmark),nameof(WithoutMethod)),
                prefix:new HarmonyMethod(AccessTools.Method(typeof(NativeAllyExecution),"Before")),
                finalizer:new HarmonyMethod(AccessTools.Method(typeof(NativeAllyExecution),"After")));
            var result=new JObject();
            foreach(var pair in new[]{(Name:"Baseline",Call:(Func<int>)probe.Baseline),(Name:"WithMethod",Call:(Func<int>)probe.WithMethod),(Name:"WithoutMethod",Call:(Func<int>)probe.WithoutMethod)})
            {
                var sum=0;for(var i=0;i<1000;i++)sum+=pair.Call();
                var timer=Stopwatch.StartNew();for(var i=0;i<100000;i++)sum+=pair.Call();timer.Stop();
                result[pair.Name]=new JObject{["Calls"]=100000,["Ms"]=timer.Elapsed.TotalMilliseconds,["Sum"]=sum};
            }
            return result;
        }
        finally{hooks.UnpatchSelf();NativeAllyExecution.Restore(saved);UnityEngine.Object.DestroyImmediate(probe);}
    }
}
#endif
