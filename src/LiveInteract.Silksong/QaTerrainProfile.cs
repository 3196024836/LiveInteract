#if LIVEINTERACT_QA
using System;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>只读对照原生射线缓存、当前射线与实体碰撞形状；不发送事件、改状态或伤害。</summary>
internal static class QaTerrainProfile
{
    /// <summary>在同一主线程快照内记录探测来源与真实物理查询。</summary>
    internal static JObject Read(RayCast2dV2 action, OwnedSummon actor)
    {
        var cached = AccessTools.Field(typeof(RayCast2dV2), "_trans").GetValue(action) as Transform;
        var declared = action.Fsm.GetOwnerDefaultTarget(action.fromGameObject);
        var origin = action.fromPosition.Value + (cached != null ? (Vector2)cached.position : Vector2.zero);
        var rawDirection = action.direction.Value;
        var direction = rawDirection.normalized;
        if (cached != null && action.space == Space.Self)
            direction = cached.TransformDirection(new Vector3(rawDirection.x, rawDirection.y, 0)).normalized;
        var mask = ActionHelpers.LayerArrayToLayerMask(action.layerMask, action.invertMask.Value);
        var filter = new ContactFilter2D {
            useTriggers = !action.ignoreTriggers.Value, useLayerMask = true, layerMask = mask,
            useDepth = !action.minDepth.IsNone || !action.maxDepth.IsNone,
            minDepth = action.minDepth.IsNone ? float.NegativeInfinity : action.minDepth.Value,
            maxDepth = action.maxDepth.IsNone ? float.PositiveInfinity : action.maxDepth.Value
        };
        var hits = new RaycastHit2D[24];
        var distance = action.distance.Value > 0 ? action.distance.Value : float.PositiveInfinity;
        var count = action.distance.Value != 0 ? Physics2D.Raycast(origin, direction, filter, hits, distance) : 0;
        var body = actor.GetComponent<Rigidbody2D>();
        var solidFilter = new ContactFilter2D { useTriggers = false, useLayerMask = true,
            layerMask = LayerMask.GetMask("Terrain") };
        var shapeRows = new JArray();
        // 原中心射线可能越过矮台阶；只读记录真实实体在左右各5厘米的接触，排除警戒触发框。
        foreach (var collider in actor.GetComponentsInChildren<Collider2D>(true))
        {
            if (body == null || collider.attachedRigidbody != body || !collider.enabled ||
                !collider.gameObject.activeInHierarchy || collider.isTrigger) continue;
            foreach (var x in new[] { -1f, 1f })
            {
                var shapeHits = new RaycastHit2D[24];
                var shapeCount = collider.Cast(new Vector2(x, 0), solidFilter, shapeHits, .05f, true);
                shapeRows.Add(new JObject { ["Collider"] = Path(collider.transform), ["DirectionX"] = x,
                    ["Hits"] = new JArray(shapeHits.Take(shapeCount).Select(Hit)) });
            }
        }
        return new JObject {
            ["CachedOrigin"] = cached != null ? Path(cached) : null,
            ["CachedOriginOwnedRoot"] = cached != null ? cached.GetComponentInParent<OwnedSummon>()?.GetInstanceID() : null,
            ["DeclaredOrigin"] = declared != null ? Path(declared.transform) : null,
            ["CacheMatchesDeclared"] = cached == (declared != null ? declared.transform : null),
            ["OriginX"] = origin.x, ["OriginY"] = origin.y,
            ["DirectionX"] = direction.x, ["DirectionY"] = direction.y,
            ["Mask"] = mask, ["UsesDepth"] = filter.useDepth,
            ["RayHits"] = new JArray(hits.Take(count).Select(Hit)),
            ["SolidShapeContacts"] = shapeRows
        };
    }

    private static JObject Hit(RaycastHit2D hit) => new() {
        ["Collider"] = hit.collider != null ? Path(hit.collider.transform) : null,
        ["Layer"] = hit.collider != null ? hit.collider.gameObject.layer : (int?)null,
        ["Trigger"] = hit.collider != null ? hit.collider.isTrigger : (bool?)null,
        ["X"] = hit.point.x, ["Y"] = hit.point.y, ["Distance"] = hit.distance,
        ["NormalX"] = hit.normal.x, ["NormalY"] = hit.normal.y
    };

    private static string Path(Transform transform)
    {
        var path = transform.name;
        while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
        return path;
    }
}
#endif
