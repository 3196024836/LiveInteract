using System.Reflection;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using LiveInteract.Contracts;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>仅校准已核对的钟卫、苔蚀朝圣者和焦油螨友军原作弹体的出手瞄准，不改敌方、不接管飞行或反弹。</summary>
[HarmonyPatch]
internal static class CompanionRangedAim
{
    /// <summary>在原作给实际弹体赋速之后修正出手方向。</summary>
    /// <returns>原作直射和抛射赋速方法。</returns>
    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SetVelocity2d),"DoSetVelocity");
        yield return AccessTools.Method(typeof(SetVelocityAsAngle),"DoSetVelocity");
    }

    /// <summary>按真实受击框中心瞄准，只更改已核对友军弹体的初速度。</summary>
    /// <param name="__instance">本次执行的原作赋速动作。</param>
    private static void Postfix(FsmStateAction __instance)
    {
        // 只处理召唤友军向外发射的本体弹药，排除自身移动、后坐力和其他怪物的散射招式。
        var ally=__instance.Owner?.GetComponentInParent<NativeEnemyAlly>();
        if(ally==null||!ally.HasEnemy)return;
        var key=ally.GetComponent<OwnedSummon>()?.AssetKey;
        if(key != "spawn.bone-spitter" && !CompanionAim.UsesTargetedArc(key))return;
        var targetDefault=__instance is SetVelocity2d arc?arc.gameObject:((SetVelocityAsAngle)__instance).gameObject;
        var projectile=__instance.Fsm.GetOwnerDefaultTarget(targetDefault);
        var marker=projectile!=null?projectile.GetComponent<NativeAllyProjectile>():null;
        var body=projectile!=null?projectile.GetComponent<Rigidbody2D>():null;
        if(marker?.Owner!=ally||projectile==ally.gameObject||body==null)return;

        // 原作针对 Hornet 的固定高度偏移不适合矮小敌人；取实际身体受击框，不取警戒范围。
        var target=ally.TargetObject;var health=target!=null?target.GetComponent<HealthManager>():null;
        if(health==null)return;
        var hitbox=health.GetComponentsInChildren<Collider2D>()
            .FirstOrDefault(c=>c.enabled&&c.gameObject.activeInHierarchy&&c.gameObject.layer==11&&c.GetComponentInParent<HealthManager>()==health);
        var point=hitbox!=null?(Vector2)hitbox.bounds.center:(Vector2)health.transform.position;
        var bulletBox=projectile!.GetComponent<Collider2D>();
        var origin=bulletBox!=null?(Vector2)bulletBox.bounds.center:(Vector2)projectile.transform.position;
        var delta=point-origin;var velocity=body.linearVelocity;
        if(key=="spawn.bone-spitter"&&__instance is SetVelocityAsAngle)
        {
            // 保留焦油弹原速，出手后不追踪或穿透目标。
            if(delta.sqrMagnitude>.0001f)body.linearVelocity=delta.normalized*velocity.magnitude;
        }
        else if(CompanionAim.UsesTargetedArc(key)&&__instance is SetVelocity2d)
        {
            // 保留原生垂直起速与重力，仅算准下降时穿过目标中心所需的横向速度。
            var speed=CompanionAim.HorizontalSpeed(delta.x,delta.y,velocity.y,Physics2D.gravity.y*body.gravityScale);
            if(speed.HasValue){velocity.x=(float)speed.Value;body.linearVelocity=velocity;}
        }
    }
}
