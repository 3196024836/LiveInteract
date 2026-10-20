using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
namespace LiveInteract.Silksong;
/// <summary>对原生敌人只适配阵营/目标/跟随；攻击伤害来自原生当前开启的DamageHero碰撞，不新增远程伤害技能。</summary>
[DefaultExecutionOrder(1000)]
internal sealed class NativeEnemyAlly : MonoBehaviour
{
    public float DamageMultiplier=1;
    public bool Flying;
    public bool PreserveNativeMovement;
    private readonly List<NativeAllyProjectile> projectiles=new();
    private PlayMakerFSM[] fsms=Array.Empty<PlayMakerFSM>();
    private readonly List<DamageHero> weapons=new();
    public void AddProjectile(DamageHero weapon,NativeAllyProjectile projectile){if(!weapons.Contains(weapon))weapons.Add(weapon);if(!projectiles.Contains(projectile))projectiles.Add(projectile);}
    public void RemoveProjectile(DamageHero weapon)=>weapons.Remove(weapon);
    private readonly List<FsmGameObject> targets=new();
    private readonly Dictionary<int,float> hits=new();
    private readonly List<Collider2D> contacts=new();
    private HealthManager? enemy;
    private GameObject? followPoint;
    private float nextTarget;
    private string scene="";
    public GameObject? TargetObject => enemy!=null && !enemy.isDead && enemy.hp>0 ? enemy.gameObject : followPoint != null ? followPoint : HeroController.SilentInstance?.gameObject;
    private void Start()
    {
        fsms=GetComponentsInChildren<PlayMakerFSM>(true);weapons.AddRange(GetComponentsInChildren<DamageHero>(true));
        followPoint=new GameObject("LiveInteract friendly follow target");followPoint.transform.SetParent(transform.parent,false);
        scene=SceneManager.GetActiveScene().name;
        Retarget();
    }
    private void Update()
    {
        if(GameActions.Current?.CanAct!=true)return;
        var hero=HeroController.SilentInstance;if(hero==null)return;
        if(scene!=SceneManager.GetActiveScene().name || Vector2.Distance(transform.position,hero.transform.position)>18)
        {scene=SceneManager.GetActiveScene().name;transform.position=hero.transform.position+new Vector3(-2,Flying?1.5f:.3f,0);var body=GetComponent<Rigidbody2D>();if(body!=null)body.linearVelocity=Vector2.zero;}
        if(followPoint!=null)followPoint.transform.position=hero.transform.position+new Vector3(-2,Flying?1.2f:0,0);
        if(Time.unscaledTime>=nextTarget){weapons.RemoveAll(w=>w==null);projectiles.RemoveAll(p=>p==null||p.Owner!=this);nextTarget=Time.unscaledTime+.25f;enemy=GameActions.Current.FindHostile(transform.position,14);}
        Retarget();
    }
    private void Retarget()
    {
        var hero=HeroController.SilentInstance;
        foreach(var fsm in fsms)
        {
            if(fsm==null)continue;
            foreach(var item in fsm.FsmVariables.GameObjectVariables)
            {
                if(targets.Contains(item))continue;
                var name=(item.Name??"").Replace(" ","").ToLowerInvariant();
                if(item.Value==hero?.gameObject || name is "hero" or "player" or "herotarget" or "target")targets.Add(item);
            }
        }
        foreach(var value in targets)value.Value=TargetObject;
    }
    private void FixedUpdate()
    {
        if(GameActions.Current?.CanAct!=true)return;
        SteerToTarget();

        // 仅检查原生武器当前实际开启的碰撞。Overlap绕开敌人层不互撞的默认矩阵，不扩大攻击范围。
        for(var weaponIndex=0;weaponIndex<weapons.Count;weaponIndex++)
        {
            var weapon=weapons[weaponIndex];
            if(weapon==null || !weapon.enabled || !weapon.gameObject.activeInHierarchy)continue;
            foreach(var box in weapon.GetComponents<Collider2D>())
            {
                if(!box.enabled)continue;
                contacts.Clear();box.Overlap(new ContactFilter2D{useTriggers=true,useLayerMask=false},contacts);
                foreach(var contact in contacts)
                {
                    if(contact==null)continue;
                    var target=contact.GetComponentInParent<HealthManager>();
                    if(target==null || target.isDead || target.hp<=0 || target.GetComponentInParent<OwnedCompanion>()!=null || target.GetComponentInParent<HeroController>()!=null)continue;
                    // 只接受身体本身的碰撞，忽略AI侦测范围等大触发器。
                    if(contact.isTrigger && contact.gameObject!=target.gameObject)continue;
                    var id=target.GetInstanceID();if(hits.TryGetValue(id,out var next) && Time.unscaledTime<next)continue;
                    hits[id]=Time.unscaledTime+.65f;
                    var damage=Math.Max(1,(int)Math.Ceiling(Math.Max(1,weapon.damageDealt)*DamageMultiplier));
                    target.Hit(new HitInstance{Source=weapon.gameObject,DamageDealt=damage,AttackType=AttackTypes.Generic,Multiplier=1,MagnitudeMultiplier=1,IsFirstHit=true,IsHeroDamage=false});
                }
            }
        }
        if(hits.Count>512){var expired=hits.Where(p=>p.Value<Time.unscaledTime).Select(p=>p.Key).ToArray();foreach(var id in expired)hits.Remove(id);}
    }
    /// <summary>原敌人检测器常只接受Hero层；以本体刚体补足转阵营后的追敌/跟随，不替换攻击状态机或高速原生攻击动作。</summary>
    private void SteerToTarget()
    {
        if(PreserveNativeMovement)return;
        var target=TargetObject;var body=GetComponent<Rigidbody2D>();if(target==null||body==null||body.bodyType!=RigidbodyType2D.Dynamic)return;
        if(body.linearVelocity.magnitude>8)return;
        var delta=(Vector2)(target.transform.position-transform.position);var desired=Vector2.zero;
        if(Flying){if(delta.magnitude>1)desired=delta.normalized*3;body.linearVelocity=Vector2.Lerp(body.linearVelocity,desired,.25f);}
        else{var vx=Mathf.Abs(delta.x)>1.2f?Mathf.Sign(delta.x)*2.5f:0;body.linearVelocity=new Vector2(vx,body.linearVelocity.y);}
    }
    private void OnDestroy(){foreach(var p in projectiles)if(p!=null&&p.Owner==this)p.Disarm();if(followPoint!=null)Object.Destroy(followPoint);}
}
/// <summary>仅替换本工具友军状态机的GetHero，不影响真正敌人和玩家。</summary>
[HarmonyPatch(typeof(GetHero),nameof(GetHero.OnEnter))]
internal static class FriendlyNativeTargetHook
{
    private static bool Prefix(GetHero __instance)
    {
        var ally=__instance.Owner?.GetComponentInParent<NativeEnemyAlly>();if(ally==null)return true;
        __instance.storeResult.Value=ally.TargetObject;__instance.Finish();return false;
    }
}
/// <summary>原生敌怪攻击脚本仍保留，但不允许它们对主播产生原阵营碰撞伤害。</summary>
[HarmonyPatch]
internal static class FriendlyNativeCollisionHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {yield return AccessTools.Method(typeof(DamageHero),"OnTriggerEnter2D");yield return AccessTools.Method(typeof(DamageHero),"OnCollisionEnter2D");}
    private static bool Prefix(DamageHero __instance)=>!NativeAllyExecution.IsFriendly(__instance.gameObject);
}
/// <summary>持续小弟不被主播/其他友军误杀；紧急停止直接销毁，不经过此伤害入口。</summary>
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.Hit))]
internal static class FriendlyNativeHealthHook
{
    private static bool Prefix(HealthManager __instance)=>__instance.GetComponentInParent<OwnedCompanion>()==null;
}
