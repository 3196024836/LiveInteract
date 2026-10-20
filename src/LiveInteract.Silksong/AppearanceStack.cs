using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using LiveInteract.Contracts;
using Object = UnityEngine.Object;
namespace LiveInteract.Silksong;

/// <summary>每次体型整活独立到期；只缩放渲染代理，不改变原生碰撞、AI和玩家操作。</summary>
internal sealed class AppearanceStack : MonoBehaviour
{
    private readonly AppearanceLayers pulses = new();
    private readonly List<Proxy> proxies = new();
    public bool Retired { get; private set; }
    public int Count => pulses.Count;
    public float Multiplier { get; private set; } = 1;
    public void Add(string who, bool grow, double amount, double seconds)
    {
        if (proxies.Count == 0)
        {
            foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                var mesh = renderer.GetComponent<MeshFilter>(); if (mesh == null) { continue; }
                var visual = new GameObject("LiveInteract appearance proxy"); visual.transform.SetParent(renderer.transform, false);
                var filter = visual.AddComponent<MeshFilter>(); filter.sharedMesh = mesh.sharedMesh;
                var copy = visual.AddComponent<MeshRenderer>(); copy.sharedMaterials = renderer.sharedMaterials;
                proxies.Add(new Proxy { Source = renderer, SourceMesh = mesh, Object = visual, Renderer = copy, Mesh = filter, OriginalForceOff = renderer.forceRenderingOff });
                renderer.forceRenderingOff = true;
            }
        }
        pulses.Add(who, grow, amount, seconds, Time.realtimeSinceStartup);
        Refresh();
    }
    public IEnumerable<string> Labels()
    {
        foreach (var p in pulses.Items.AsEnumerable().Reverse().Take(3)) { yield return p.Who + " · " + (p.Grow ? "变大 " : "变小 ") + (double.IsPositiveInfinity(p.End) ? "永久" : Math.Max(0, p.End - Time.realtimeSinceStartup).ToString("0.0") + "秒"); }
        if (pulses.Count > 3) { yield return "共 " + pulses.Count + " 层"; }
    }
    private void LateUpdate()
    {
        if (Retired) { return; }
        pulses.Tick(Time.realtimeSinceStartup);
        if (pulses.Count == 0) { Restore(); Object.Destroy(this); return; }
        Refresh();
    }
    private void Refresh()
    {
        Multiplier = (float)pulses.Multiplier;
        foreach (var p in proxies)
        {
            if (p.Source == null || p.Renderer == null) { continue; }
            p.Source.forceRenderingOff = true; p.Mesh.sharedMesh = p.SourceMesh.sharedMesh;
            p.Renderer.sharedMaterials = p.Source.sharedMaterials; p.Renderer.sortingLayerID = p.Source.sortingLayerID; p.Renderer.sortingOrder = p.Source.sortingOrder;
            p.Renderer.enabled = p.Source.enabled && p.Source.gameObject.activeInHierarchy;
            p.Object.transform.localScale = Vector3.one * Multiplier;
        }
    }
    public void Restore()
    {
        Retired = true;
        foreach (var p in proxies) { if (p.Source != null) { p.Source.forceRenderingOff = p.OriginalForceOff; } if (p.Object != null) { Object.Destroy(p.Object); } }
        proxies.Clear(); pulses.Clear();
    }
    private void OnDestroy() => Restore();
    private sealed class Proxy
    {
        public MeshRenderer Source = null!, Renderer = null!; public MeshFilter SourceMesh = null!, Mesh = null!;
        public GameObject Object = null!; public bool OriginalForceOff;
    }
}

/// <summary>只标记本工具的持续援军；不改变游戏原生工具随从。</summary>
internal sealed class OwnedCompanion : MonoBehaviour { }
