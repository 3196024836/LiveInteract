using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
namespace LiveInteract.Silksong;
/// <summary>对原生敌人只适配阵营/目标/跟随；承伤走原生HealthManager，敌人攻击与接触伤害照常生效。</summary>
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
    private readonly Dictionary<int,float> incoming=new();
    private readonly List<Collider2D> contacts=new();
    private readonly List<Collider2D> ownColliders=new();
    private HealthManager? enemy;
    private const float FollowDistance = 3f; // 与主播保持的距离，进入这个范围就停下。
    private HealthManager? health;
    private GameObject? followPoint;
    private Rigidbody2D? body;
    private float nextTarget;
    private float nextWeaponScan;
    private float nextSelfHit;
    private float nearSince;
    private Vector3 lastHeroPosition;
    private string scene="";
    /// <summary>跟随到位后把目标指回自己：原生移动没有再往前走的方向，小弟就会停在主播身边。</summary>
    public GameObject? TargetObject
    {
        get
        {
            if(enemy!=null && !enemy.isDead && enemy.hp>0)return enemy.gameObject;
            var hero=HeroController.SilentInstance;
            if(followPoint!=null && hero!=null && Vector2.Distance(transform.position,hero.transform.position)>FollowDistance)return followPoint;
            return gameObject;
        }
    }
    private void Start()
    {
        fsms=GetComponentsInChildren<PlayMakerFSM>(true);weapons.AddRange(GetComponentsInChildren<DamageHero>(true));
        ownColliders.AddRange(GetComponentsInChildren<Collider2D>(true).Where(item=>item!=null));
        health=GetComponentInChildren<HealthManager>();body=GetComponent<Rigidbody2D>();
        // 友军本体上的原生伤害组件一律关闭：伤害改由 StrikeEnemies 结算，避免任何路径碰伤主播。
        foreach(var weapon in weapons)if(weapon!=null)weapon.enabled=false;
        followPoint=new GameObject("LiveInteract friendly follow target");followPoint.transform.SetParent(transform.parent,false);
        scene=SceneManager.GetActiveScene().name;
        lastHeroPosition=transform.position;nearSince=Time.unscaledTime;
        Retarget();
    }
    private void Update()
    {
        var hero=HeroController.SilentInstance;if(hero==null)return;
        var heroPosition=hero.transform.position;
        // 传送、重生和切场景会让角色瞬移；这些时候直接归位，避免友军留在旧位置。
        var warped=(heroPosition-lastHeroPosition).sqrMagnitude>25f;lastHeroPosition=heroPosition;
        var sceneNow=SceneManager.GetActiveScene().name;if(scene!=sceneNow){scene=sceneNow;warped=true;}
        var distance=Vector2.Distance(transform.position,heroPosition);
        if(warped||distance>9f)SnapTo(heroPosition);
        else if(distance>6f&&Time.unscaledTime-nearSince>2f)SnapTo(heroPosition); // 追上超过两秒仍然拉远：判定卡住。
        if(Vector2.Distance(transform.position,heroPosition)<=6f)nearSince=Time.unscaledTime;
        if(followPoint!=null)followPoint.transform.position=heroPosition+new Vector3(-2,Flying?1.2f:0,0);
        if(GameActions.Current?.CanAct!=true)return;
        if(Time.unscaledTime>=nextTarget){weapons.RemoveAll(w=>w==null);projectiles.RemoveAll(p=>p==null||p.Owner!=this);nextTarget=Time.unscaledTime+.25f;enemy=GameActions.Current.FindHostile(transform.position,14);}
        if(Time.unscaledTime>=nextWeaponScan)
        {
            // 后加进来的子物体（飞行道具、特效）同样收编并关掉原生伤害组件。
            nextWeaponScan=Time.unscaledTime+.5f;
            foreach(var weapon in GetComponentsInChildren<DamageHero>(true))
            {
                if(weapon==null||weapons.Contains(weapon))continue;
                weapons.Add(weapon);weapon.enabled=false;
            }
        }
        Retarget();
    }
    /// <summary>把友军直接放到角色身侧；重生、传送和场景切换后都走这里，并同步物理位置。</summary>
    public void SnapTo(Vector3 heroPosition)
    {
        var target=heroPosition+new Vector3(-2.2f,Flying?1.6f:.4f,0);
        var rigidbody=body;
        if(rigidbody!=null){rigidbody.linearVelocity=Vector2.zero;rigidbody.position=target;rigidbody.WakeUp();}
        transform.position=target;Physics2D.SyncTransforms();
        if(followPoint!=null)followPoint.transform.position=target;
        nearSince=Time.unscaledTime;
    }
    public void SnapToHero(){var hero=HeroController.SilentInstance;if(hero!=null)SnapTo(hero.transform.position);}
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
        // 原生伤害组件始终保持关闭：不可操作时也要压住，状态机随时可能把它重新打开。
        for(var index=0;index<weapons.Count;index++){var held=weapons[index];if(held!=null&&held.enabled)held.enabled=false;}
        if(GameActions.Current?.CanAct!=true)return;
        SteerToTarget();
        StrikeEnemies();
        TakeEnemyHits();
    }
    /// <summary>友军的伤害一律自己结算：原生DamageHero保持关闭，绝不走主角受伤入口，碰撞范围仍按原生实际开启的碰撞。</summary>
    private void StrikeEnemies()
    {
        for(var weaponIndex=0;weaponIndex<weapons.Count;weaponIndex++)
        {
            var weapon=weapons[weaponIndex];
            if(weapon==null || !weapon.gameObject.activeInHierarchy)continue;
            if(weapon.enabled)weapon.enabled=false; // 状态机可能重新打开原生开关，每帧再关一次。
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
    /// <summary>敌人贴在身上的攻击/接触碰撞照常打在友军本体；只忽略主播和其他友军来源，避免友军被主播误杀。</summary>
    private void TakeEnemyHits()
    {
        var target=health;if(target==null||target.isDead||target.hp<=0)return;
        if(Time.unscaledTime<nextSelfHit)return;
        nextSelfHit=Time.unscaledTime+.1f;
        foreach(var own in ownColliders)
        {
            if(own==null||!own.enabled||!own.gameObject.activeInHierarchy)continue;
            contacts.Clear();own.Overlap(new ContactFilter2D{useTriggers=true,useLayerMask=false},contacts);
            foreach(var contact in contacts)
            {
                if(contact==null)continue;
                var weapon=contact.GetComponentInParent<DamageHero>();
                if(weapon==null||!weapon.enabled||!weapon.gameObject.activeInHierarchy)continue;
                if(weapon.gameObject==gameObject||NativeAllyExecution.IsHeroOwned(weapon.gameObject))continue;
                var id=weapon.GetInstanceID();if(incoming.TryGetValue(id,out var next)&&Time.unscaledTime<next)continue;
                incoming[id]=Time.unscaledTime+.8f;
                target.Hit(new HitInstance{Source=weapon.gameObject,DamageDealt=Math.Max(1,weapon.damageDealt),AttackType=AttackTypes.Generic,Multiplier=1,MagnitudeMultiplier=1,IsFirstHit=true,IsHeroDamage=false});
                if(target.isDead)return;
            }
        }
        if(incoming.Count>64){foreach(var id in incoming.Where(p=>p.Value<Time.unscaledTime).Select(p=>p.Key).ToArray())incoming.Remove(id);}
    }
    /// <summary>原敌人检测器常只接受Hero层；以本体刚体补足转阵营后的追敌/跟随，不替换攻击状态机或高速原生攻击动作。</summary>
    private void SteerToTarget()
    {
        if(PreserveNativeMovement)return;
        var rigidbody=body;if(rigidbody==null||rigidbody.bodyType!=RigidbodyType2D.Dynamic)return;
        var target=TargetObject;
        if(target==null||target==gameObject)
        {
            // 已经到位或没有目标：原地停住，不再往前挤，也不再左右晃。
            var velocity=rigidbody.linearVelocity;
            var rest=Flying?Vector2.Lerp(velocity,Vector2.zero,.4f):new Vector2(0,velocity.y);
            if(Flying&&rest.sqrMagnitude<0.25f)rest=Vector2.zero;
            rigidbody.linearVelocity=rest;
            return;
        }
        if(rigidbody.linearVelocity.magnitude>8)return;
        var delta=(Vector2)(target.transform.position-transform.position);
        if(Flying)
        {
            var desired=delta.sqrMagnitude>1f?delta.normalized*Mathf.Min(3,delta.magnitude*2):Vector2.zero;
            rigidbody.linearVelocity=Vector2.Lerp(rigidbody.linearVelocity,desired,.3f);
        }
        else
        {
            var vx=Mathf.Abs(delta.x)>1.2f?Mathf.Sign(delta.x)*2.5f:0;
            rigidbody.linearVelocity=new Vector2(vx,rigidbody.linearVelocity.y);
        }
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
/// <summary>友军不把主播当攻击目标：原生视野检测对友军恒为“看不见”。</summary>
[HarmonyPatch]
internal static class FriendlySeeHeroHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GetCanSeeHero),nameof(GetCanSeeHero.OnEnter));
        yield return AccessTools.Method(typeof(GetCanSeeHero),nameof(GetCanSeeHero.OnUpdate));
        yield return AccessTools.Method(typeof(GetCanSeeHeroV2),nameof(GetCanSeeHeroV2.OnEnter));
        yield return AccessTools.Method(typeof(GetCanSeeHeroV2),nameof(GetCanSeeHeroV2.OnUpdate));
    }
    private static bool Prefix(FsmStateAction __instance)
    {
        if(__instance.Owner==null||__instance.Owner.GetComponentInParent<NativeEnemyAlly>()==null)return true;
        if(__instance is GetCanSeeHero first){first.storeResult.Value=false;}
        else if(__instance is GetCanSeeHeroV2 second){second.storeResult.Value=false;}
        __instance.Finish(); // 直接结束这一帧，事件不触发。
        return false;
    }
}
/// <summary>朝向与贴身站位脚本在友军身上不执行，避免朝主播挥击或贴到主播身上。</summary>
[HarmonyPatch]
internal static class FriendlyHeroAttachHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MatchHeroFacing),"DoMatch");
        yield return AccessTools.Method(typeof(PositionToHeroClamped),"Update");
    }
    private static bool Prefix(MonoBehaviour __instance)=>__instance.GetComponentInParent<NativeEnemyAlly>()==null;
}
/// <summary>友军可以承受敌人伤害；主播与其他友军的命中仍然不计，避免自己人误杀。</summary>
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.Hit))]
internal static class FriendlyNativeHealthHook
{
    private static bool Prefix(HealthManager __instance,HitInstance __0)=>__instance.GetComponentInParent<OwnedCompanion>()==null||!NativeAllyExecution.IsHeroHit(__0);
}
