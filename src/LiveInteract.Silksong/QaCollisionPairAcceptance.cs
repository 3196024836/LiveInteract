#if LIVEINTERACT_QA
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>验证物理忽略的真实阵营切换、启停与释放；不参与战斗验收。</summary>
internal sealed partial class QaHarness
{
    /// <summary>只读列出友军实体与主播身体、独立推挤体的实际碰撞关系。</summary>
    /// <returns>实体位置、忽略关系和碰撞层规则。</returns>
    private JObject ReadFriendlyHeroCollisions()
    {
        // 只取当前主播及其直接持有的推挤体，不扫描或接管无关场景对象。
        var hero = HeroController.SilentInstance;
        var pusher = AccessTools.Field(typeof(HeroController), "heroPhysicsPusher").GetValue(hero) as GameObject;
        var playerColliders = hero.GetComponentsInChildren<Collider2D>(true)
            .Concat(pusher != null ? pusher.GetComponentsInChildren<Collider2D>(true) : System.Array.Empty<Collider2D>()).ToArray();
        var pairs = new JArray();
        foreach (var ally in UnityEngine.Object.FindObjectsByType<NativeEnemyAlly>(FindObjectsSortMode.None))
        foreach (var own in ally.GetComponentsInChildren<Collider2D>(true).Where(c => c.enabled && c.gameObject.activeInHierarchy))
        foreach (var player in playerColliders.Where(c => (!c.isTrigger || c.GetComponent<HeroBox>() != null) && c.enabled && c.gameObject.activeInHierarchy))
        {
            pairs.Add(new JObject { ["ActorId"] = ally.GetComponent<OwnedSummon>()?.GetInstanceID(),
                ["Asset"] = ally.GetComponent<OwnedSummon>()?.AssetKey,
                ["Own"] = Hierarchy(own.transform), ["Player"] = Hierarchy(player.transform),
                ["OwnLayer"] = own.gameObject.layer, ["OwnTrigger"] = own.isTrigger, ["PlayerLayer"] = player.gameObject.layer,
                ["OwnX"] = own.bounds.center.x, ["OwnY"] = own.bounds.center.y,
                ["PlayerX"] = player.bounds.center.x, ["PlayerY"] = player.bounds.center.y,
                ["Separation"] = own.Distance(player).distance,
                ["Touching"] = own.IsTouching(player),
                ["Ignored"] = Physics2D.GetIgnoreCollision(own, player),
                ["LayersIgnored"] = Physics2D.GetIgnoreLayerCollision(own.gameObject.layer, player.gameObject.layer) });
        }
        return new JObject { ["Pairs"] = pairs };
    }

    /// <summary>在隔离场景创建两个短命碰撞体，检查原始碰撞设置能够完整恢复。</summary>
    private JObject ProbeFriendlyCollisionPairs()
    {
        var baseline = FriendlyCollisionPairs.Count;
        var a = new GameObject("QA collision pair A"); var b = new GameObject("QA collision pair B");
        var checks = new JObject();
        try
        {
            a.transform.position = HeroController.SilentInstance.transform.position + new Vector3(35, 12, 0);
            b.transform.position = a.transform.position + Vector3.right * 3;
            a.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            b.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
            var first = a.AddComponent<BoxCollider2D>(); var second = b.AddComponent<BoxCollider2D>();
            first.isTrigger = true; second.isTrigger = true;
            var left = a.AddComponent<NativeAllyProjectile>(); var right = b.AddComponent<NativeAllyProjectile>();
            checks["initially_collides"] = !Physics2D.GetIgnoreCollision(first, second);
            left.Assign(null, a, true); right.Assign(null, b, true);
            checks["friend_pair_ignored"] = Physics2D.GetIgnoreCollision(first, second);
            // 独立推挤体不在主播层级里；直接检查实际游戏实例，不据组件名猜结果。
            var hero = HeroController.SilentInstance;
            var pusher = AccessTools.Field(typeof(HeroController), "heroPhysicsPusher").GetValue(hero) as GameObject;
            var heroCollider = hero.GetComponent<Collider2D>();
            var pusherCollider = pusher != null ? pusher.GetComponent<Collider2D>() : null;
            checks["real_hero_pusher_exists"] = pusherCollider != null;
            first.enabled = false; first.enabled = true; left.IgnoreHero();
            checks["hero_ignore_reapplied_after_collider_enable"] = Physics2D.GetIgnoreCollision(first, heroCollider);
            left.Assign(null, a, false);
            checks["enemy_assignment_restores_hero_collision"] = !Physics2D.GetIgnoreCollision(first, heroCollider);
            checks["enemy_assignment_restores_pusher_collision"] = pusherCollider != null && !Physics2D.GetIgnoreCollision(first, pusherCollider);
            left.Assign(null, a, true);

            right.Assign(null, b, false);
            checks["enemy_reassignment_restores_collision"] = !Physics2D.GetIgnoreCollision(first, second);
            checks["enemy_reassignment_removes_pair"] = FriendlyCollisionPairs.Count == baseline;
            right.Assign(null, b, true);
            second.enabled = false; second.enabled = true; FriendlyCollisionPairs.Refresh(right);
            checks["collider_reenable_reapplies_ignore"] = Physics2D.GetIgnoreCollision(first, second);
            b.SetActive(false); b.SetActive(true); FriendlyCollisionPairs.Refresh(right);
            checks["pool_reenable_reapplies_ignore"] = Physics2D.GetIgnoreCollision(first, second);
            right.Assign(null, b, false); Physics2D.IgnoreCollision(first, second, true);
            right.Assign(null, b, true); right.Assign(null, b, false);
            checks["preexisting_ignore_preserved"] = Physics2D.GetIgnoreCollision(first, second);
            right.Assign(null, b, true); UnityEngine.Object.DestroyImmediate(b);
            checks["destroy_releases_pairs"] = FriendlyCollisionPairs.Count == baseline;
        }
        finally
        {
            if (a != null) { UnityEngine.Object.DestroyImmediate(a); }
            if (b != null) { UnityEngine.Object.DestroyImmediate(b); }
        }
        checks["cleanup"] = FriendlyCollisionPairs.Count == baseline;
        return new JObject { ["BaselinePairs"] = baseline, ["Checks"] = checks };
    }
}
#endif

