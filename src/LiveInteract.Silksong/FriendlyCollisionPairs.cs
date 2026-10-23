using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>友方感应区不再互相触发；忽略关系只属于当前召唤阵营，回收或变阵营时恢复。</summary>
internal static class FriendlyCollisionPairs
{
    // 只收录有独立碰撞体的友方本体和投射物。
    private static readonly Dictionary<NativeAllyProjectile, Collider2D[]> Members = new();
    private static readonly Dictionary<(int, int), Pair> Pairs = new();
    internal static int Count => Pairs.Count;

    /// <summary>保留原有忽略状态，避免移除召唤物时覆盖其他系统的设置。</summary>
    private sealed class Pair
    {
        internal NativeAllyProjectile First = null!, Second = null!;
        internal Collider2D A = null!, B = null!;
        internal bool WasIgnored;
    }

    /// <summary>收编新增身体/弹体碰撞，重应用停用再启用后丢失的忽略关系。</summary>
    /// <param name="member">具有明确来源的友方标记。</param>
    internal static void Refresh(NativeAllyProjectile? member)
    {
        if (member == null || !member.WasFriendly) { return; }
        var own = member.GetComponentsInChildren<Collider2D>(true)
            .Where(c => c.GetComponentInParent<NativeAllyProjectile>() == member).ToArray();
        if (own.Length == 0) { return; }
        Members[member] = own;

        // 无序碰撞对只保存一次；不修改碰撞层、不忽略地形或真正敌人。
        foreach (var other in Members.ToArray())
        {
            if (other.Key == null || other.Key == member || !other.Key.WasFriendly) { continue; }
            foreach (var a in own)
            foreach (var b in other.Value)
            {
                if (a == null || b == null || a == b || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) { continue; }
                var first = a.GetInstanceID(); var second = b.GetInstanceID();
                var key = first < second ? (first, second) : (second, first);
                if (!Pairs.TryGetValue(key, out var pair))
                {
                    pair = new Pair { First = member, Second = other.Key, A = a, B = b,
                        WasIgnored = Physics2D.GetIgnoreCollision(a, b) };
                    Pairs.Add(key, pair);
                }
                if (a.enabled && b.enabled && !Physics2D.GetIgnoreCollision(a, b)) { Physics2D.IgnoreCollision(a, b, true); }
            }
        }
    }

    /// <summary>本体消失或对象池交回敌军时，撤回与它相关的全部关系。</summary>
    /// <param name="member">离开友方阵营的标记。</param>
    internal static void Remove(NativeAllyProjectile member)
    {
        // 两端任意一端变阵营，都恢复这一对；不会留下“敌军打不到友军”的状态。
        foreach (var item in Pairs.Where(p => ReferenceEquals(p.Value.First, member) || ReferenceEquals(p.Value.Second, member)).ToArray())
        {
            var pair = item.Value;
            if (pair.A != null && pair.B != null) { Physics2D.IgnoreCollision(pair.A, pair.B, pair.WasIgnored); }
            Pairs.Remove(item.Key);
        }
        Members.Remove(member);
    }
}
