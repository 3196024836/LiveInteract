#if LIVEINTERACT_QA
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace LiveInteract.Silksong;

internal sealed partial class QaHarness
{
    /// <summary>有界单变量诊断，不作战斗验收：比较绘制、物理过滤和恢复后的真实帧率。</summary>
    private IEnumerator RunLoadIsolation(LiveInteract.Contracts.ActionResult result)
    {
        var renderers=UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
            .Where(r=>NativeAllyExecution.IsFriendly(r.gameObject)).Select(r=>(Item:r,Enabled:r.enabled)).ToArray();
        var colliders=UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None)
            .Where(c=>NativeAllyExecution.IsFriendly(c.gameObject)).Select(c=>c.gameObject).Distinct()
            .Select(g=>(Item:g,Layer:g.layer)).ToArray();
        var used=UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Select(t=>t.gameObject.layer).ToHashSet();
        var layer=Enumerable.Range(8,24).FirstOrDefault(i=>!used.Contains(i));
        if(layer==0){result.Success=false;result.Message="No unused diagnostic layer";bridge.Send(new(){Kind="result",Result=result});yield break;}
        var cameras=Camera.allCameras.Select(c=>(Item:c,Mask:c.cullingMask)).ToArray();
        var ignore=layer!=0&&Physics2D.GetIgnoreLayerCollision(layer,layer);
        var phases=new JArray();
        try
        {
            foreach(var phase in new[]{"Baseline","RenderingOff","LayerFilter","Restored"})
            {
                foreach(var renderer in renderers)if(renderer.Item!=null)renderer.Item.enabled=phase=="RenderingOff"?false:renderer.Enabled;
                foreach(var collider in colliders)if(collider.Item!=null)collider.Item.layer=phase=="LayerFilter"&&layer!=0?layer:collider.Layer;
                if(layer!=0)Physics2D.IgnoreLayerCollision(layer,layer,phase=="LayerFilter"||ignore);
                foreach(var camera in cameras)if(camera.Item!=null)camera.Item.cullingMask=phase=="LayerFilter"?camera.Mask|(1<<layer):camera.Mask;
                yield return new WaitForSecondsRealtime(.5f);
                var started=Time.realtimeSinceStartupAsDouble;var first=Time.frameCount;var allFocused=true;
                var frames=0;
                while(Time.realtimeSinceStartupAsDouble-started<5)
                {
                    allFocused&=Application.isFocused;frames++;
                    yield return null;
                }
                phases.Add(new JObject{["Phase"]=phase,["Seconds"]=Time.realtimeSinceStartupAsDouble-started,
                    ["Frames"]=Time.frameCount-first,["Focused"]=allFocused});
            }
            result.Success=true;result.Message=new JObject{["Phases"]=phases,["RendererCount"]=renderers.Length,["ColliderObjects"]=colliders.Length,["DiagnosticLayer"]=layer}.ToString(Formatting.None);
        }
        finally
        {
            foreach(var renderer in renderers)if(renderer.Item!=null)renderer.Item.enabled=renderer.Enabled;
            foreach(var collider in colliders)if(collider.Item!=null)collider.Item.layer=collider.Layer;
            if(layer!=0)Physics2D.IgnoreLayerCollision(layer,layer,ignore);
            foreach(var camera in cameras)if(camera.Item!=null)camera.Item.cullingMask=camera.Mask;
        }
        bridge.Send(new(){Kind="result",Result=result});
    }
}
#endif
