#if LIVEINTERACT_QA
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using HutongGames.PlayMaker;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LiveInteract.Silksong;

/// <summary>仅隔离验收编译：分开观察实际敌方碰撞、玩家友伤及友军对玩家的全部扣血入口。</summary>
internal sealed partial class QaHarness
{
    private GameObject ActorRoot(int id)
    {
        RequireIsolation();
        return Resources.FindObjectsOfTypeAll<OwnedCompanion>().Select(x=>x.gameObject)
            .Concat(Resources.FindObjectsOfTypeAll<OwnedSummon>().Select(x=>x.gameObject))
            .First(x=>x.scene.IsValid()&&x.GetInstanceID()==id);
    }
    private static HealthManager? PrimaryHealth(GameObject actor)=>
        (actor.GetComponent<OwnedSummon>() is {} owned ? AccessTools.Field(typeof(OwnedSummon),"primaryHealth")?.GetValue(owned) as HealthManager : null)
        ??actor.GetComponentInChildren<HealthManager>(true);

    private JObject FriendlySnapshot()
    {
        RequireIsolation();var hero=HeroController.SilentInstance;var data=PlayerData.instance;
        var actors=Resources.FindObjectsOfTypeAll<OwnedSummon>().Select(a=>a.gameObject)
            .Concat(Resources.FindObjectsOfTypeAll<OwnedCompanion>().Select(a=>a.gameObject))
            .Where(a=>a.scene.IsValid()).Distinct().ToArray();
        return new JObject{["CanAct"]=actions.CanAct,["TimeScale"]=Time.timeScale,["Hp"]=data.health,
            ["BlueHp"]=data.healthBlue,["Invincible"]=actions.Invincible,["HeroDead"]=hero.cState.dead,
            ["Actors"]=new JArray(actors.Select(a=>
            {
                var h=PrimaryHealth(a);var native=a.GetComponent<NativeEnemyAlly>();
                return new JObject{["Id"]=a.GetInstanceID(),["Asset"]=a.GetComponent<OwnedSummon>()?.AssetKey,
                    ["Name"]=a.name,["Friendly"]=a.GetComponent<OwnedCompanion>()!=null,["Active"]=a.activeInHierarchy,
                    ["Hp"]=h?.hp,["Dead"]=h?.isDead,["Invincible"]=h?.IsInvincible,
                    ["X"]=a.transform.position.x,["Y"]=a.transform.position.y,
                    ["Target"]=native?.TargetObject?.name,["TargetHero"]=native?.TargetObject?.GetComponentInParent<HeroController>()!=null,
                    ["HasEnemy"]=native?.HasEnemy,["HealthCount"]=a.GetComponentsInChildren<HealthManager>(true).Length,
                    ["HeroFsmTargets"]=new JArray(a.GetComponentsInChildren<PlayMakerFSM>(true)
                        .SelectMany(f=>f.FsmVariables.GameObjectVariables.Where(v=>v.Value!=null&&v.Value.GetComponentInParent<HeroController>()!=null)
                            .Select(v=>f.FsmName+"/"+v.Name))),
                    ["EnabledWeapons"]=a.GetComponentsInChildren<DamageHero>(true).Count(w=>w.enabled&&w.gameObject.activeInHierarchy&&w.damageDealt>0)};
            })),["DamageEvents"]=new JArray(damageLog.Select(x=>x.DeepClone()))};
    }

    private async Task<JObject> FriendlyHitMeasured(JObject command)
    {
        var actor=ActorRoot((int)command["ActorId"]!);var health=PrimaryHealth(actor)
            ??throw new InvalidOperationException("召唤物没有可承伤的生命组件");
        var before=health.hp;var deadBefore=health.isDead;
        var fromHero=(bool?)command["HeroSource"]??true;
        if(hitSource==null){hitSource=new GameObject("QA enemy hit source");hitSource.AddComponent<DamageHero>();}
        var source=fromHero?HeroController.SilentInstance.gameObject:hitSource;
        health.Hit(new HitInstance{Source=source,DamageDealt=(int?)command["Damage"]??1,Multiplier=1,
            MagnitudeMultiplier=1,IsFirstHit=true,IsHeroDamage=fromHero,IgnoreInvulnerable=true,
            AttackType=fromHero?AttackTypes.Nail:AttackTypes.Generic});
        var after=health.hp;var deadAfter=health.isDead;
        await Task.Delay(100);
        return new JObject{["BeforeHp"]=before,["AfterHp"]=after,["BeforeDead"]=deadBefore,["AfterDead"]=deadAfter,
            ["HeroSource"]=fromHero,["ActorAlive"]=actor!=null&&actor.activeInHierarchy,["State"]=FriendlySnapshot()};
    }

    private JObject FriendlyGuardProbe(JObject command)
    {
        var actor=ActorRoot((int)command["ActorId"]!);var hero=HeroController.SilentInstance;var data=PlayerData.instance;
        var savedHealth=data.health;data.healthBlue=0;
        var positiveBefore=data.health;data.TakeHealth(1,false,false);var positiveAfter=data.health;data.health=savedHealth;
        var rows=new JArray();
        var methods=typeof(HeroController).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
            .Where(m=>m.Name is "TakeDamage" or "TakeQuickDamage" or "TakeQuickDamageSimple" or "DoSpecialDamage" or "TakeHealth");
        foreach(var method in methods)
        {
            var previous=NativeAllyExecution.Capture();var before=data.health;string? error=null;
            // 同一主线程调用前后读当前敌人，精确区分伤害旁路与两次QA请求之间的自然交战。
            var ally=actor.GetComponent<NativeEnemyAlly>();
            var target=ally!=null&&ally.HasEnemy?ally.TargetObject?.GetComponent<HealthManager>():null;
            var targetBefore=target!=null?target.hp:(int?)null;
            NativeAllyExecution.Restore(new NativeCallState{Ally=actor.GetComponent<NativeEnemyAlly>(),Origin=actor,
                FallbackTarget=actor,Spawned=true,Friendly=true,ResourceKey=actor.GetComponent<OwnedSummon>()?.AssetKey});
            try
            {
                var args=method.GetParameters().Select(p=>p.ParameterType==typeof(GameObject)?(object)actor:
                    p.ParameterType==typeof(int)?1:p.ParameterType==typeof(bool)?false:
                    p.ParameterType==typeof(string)?"QA native friendly origin":
                    p.ParameterType.IsValueType?Activator.CreateInstance(p.ParameterType):null).ToArray();
                method.Invoke(hero,args);
            }
            catch(Exception ex){error=(ex.InnerException??ex).ToString();}
            finally{NativeAllyExecution.Restore(previous);}
            rows.Add(new JObject{["Method"]=method.ToString(),["BeforeHp"]=before,["AfterHp"]=data.health,["Error"]=error,
                ["TargetId"]=target!=null?target.GetInstanceID():0,["TargetBeforeHp"]=targetBefore,["TargetAfterHp"]=target!=null?target.hp:(int?)null});
        }
        var state=NativeAllyExecution.Capture();var rawBefore=data.health;
        NativeAllyExecution.Restore(new NativeCallState{Ally=actor.GetComponent<NativeEnemyAlly>(),Origin=actor,
            FallbackTarget=actor,Spawned=true,Friendly=true});
        try{data.TakeHealth(1,false,false);}finally{NativeAllyExecution.Restore(state);}
        rows.Add(new JObject{["Method"]="PlayerData.TakeHealth in native friendly scope",["BeforeHp"]=rawBefore,["AfterHp"]=data.health});
        return new JObject{["PositiveBeforeHp"]=positiveBefore,["PositiveAfterHp"]=positiveAfter,
            ["PlayerCanTakeDamage"]=positiveAfter==positiveBefore-1,["Invincible"]=actions.Invincible,["Routes"]=rows,
            ["AllBlocked"]=rows.All(r=>(int)r["BeforeHp"]! == (int)r["AfterHp"]! && string.IsNullOrEmpty((string?)r["Error"]))};
    }

    /// <summary>生成敌人后在同一主线程续体内准备夹具，避免网络请求间被友军打死。</summary>
    private async Task<JObject> PrepareContactEnemy()
    {
        RequireIsolation();
        var existing=Resources.FindObjectsOfTypeAll<OwnedSummon>().Select(x=>x.GetInstanceID()).ToHashSet();
        await actions.ScreenSummonAsync(this, "spawn.bone-goomba", false, 8);
        var enemy=Resources.FindObjectsOfTypeAll<OwnedSummon>()
            .First(x=>x.gameObject.scene.IsValid()&&!existing.Contains(x.GetInstanceID())&&x.GetComponent<OwnedCompanion>()==null).gameObject;
        PrepareEnemyActor(enemy);
        foreach(var weapon in enemy.GetComponentsInChildren<DamageHero>(true)){weapon.enabled=false;weapon.damageDealt=0;}
        return new JObject{["EnemyId"]=enemy.GetInstanceID(),["State"]=FriendlySnapshot()};
    }

    private static void PrepareEnemyActor(GameObject enemy)
    {
        var health=PrimaryHealth(enemy)??throw new InvalidOperationException("敌方夹具缺少生命组件");
        if(!enemy.activeSelf)enemy.SetActive(true);
        foreach(var box in enemy.GetComponents<Collider2D>())box.enabled=true;
        health.hp=1000000;
        // 攻击源是耐久测试夹具：仅它保持原生无敌，受测友军的生命/无敌状态不改动。
        AccessTools.Field(typeof(HealthManager),"invincible")?.SetValue(health,true);
        foreach(var script in enemy.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if(script!=null&&(script is PlayMakerFSM||script.GetType().Assembly==typeof(HealthManager).Assembly)&&
                script is not HealthManager&&script is not DamageHero&&script is not tk2dSpriteAnimator)script.enabled=false;
        }
        var body=enemy.GetComponent<Rigidbody2D>();
        if(body!=null){body.linearVelocity=Vector2.zero;body.bodyType=RigidbodyType2D.Kinematic;}
    }

    /// <summary>隔离档内直接运行原版结局动作，确认不是仅跳过PlayerData的SetBool。</summary>
    private JObject EndingWriteProbe(JObject command)
    {
        var actor=ActorRoot((int)command["ActorId"]!);var data=PlayerData.instance;
        var completed=data.CompletedEndings;var last=data.LastCompletedEnding;var blockedBefore=NativeAllyExecution.StoryBlockedCount;
        var previous=NativeAllyExecution.Capture();
        var candidate=(SaveSlotCompletionIcons.CompletionState)((int)completed|1);
        var action=new HutongGames.PlayMaker.Actions.SetEndingCompleted
            {EndingType=new FsmEnum{Value=candidate}};
        try
        {
            NativeAllyExecution.Restore(new NativeCallState{Ally=actor.GetComponent<NativeEnemyAlly>(),Origin=actor,
                FallbackTarget=actor,Spawned=true,Friendly=actor.GetComponent<OwnedCompanion>()!=null});
            action.OnEnter();
            return new JObject{["BeforeCompleted"]=(int)completed,["AfterCompleted"]=(int)data.CompletedEndings,
                ["BeforeLast"]=(int)last,["AfterLast"]=(int)data.LastCompletedEnding,
                ["Unchanged"]=completed==data.CompletedEndings&&last==data.LastCompletedEnding,
                ["BlockedDelta"]=NativeAllyExecution.StoryBlockedCount-blockedBefore};
        }
        finally{NativeAllyExecution.Restore(previous);data.CompletedEndings=completed;data.LastCompletedEnding=last;}
    }

    private void ConfigureHostileContact(JObject command)
    {
        var ally=ActorRoot((int)command["AllyId"]!);var enemy=ActorRoot((int)command["EnemyId"]!);
        if(ally.GetComponent<OwnedCompanion>()==null||enemy.GetComponent<OwnedCompanion>()!=null)
            throw new InvalidOperationException("碰撞夹具必须是真实敌方单位攻击真实友军");
        var health=PrimaryHealth(ally)??throw new InvalidOperationException("友军没有生命组件");
        var hurt=health.GetComponentsInChildren<Collider2D>(true)
            .Concat(ally.GetComponentsInChildren<Collider2D>(true)).FirstOrDefault(c=>c.enabled&&c.gameObject.activeInHierarchy&&
                (!c.isTrigger||c.gameObject.layer==11||c.gameObject==health.gameObject))
; // 原作隐身/潜地阶段允许暂时没有受击框，真实接触验证等待下一次出现。
        PrepareEnemyActor(enemy);
        var weapons=enemy.GetComponentsInChildren<DamageHero>(true);
        var weapon=weapons.FirstOrDefault(w=>w.gameObject.activeInHierarchy&&w.GetComponents<Collider2D>().Any(c=>c.enabled))
            ??throw new InvalidOperationException("敌方夹具缺少原作攻击碰撞体");
        foreach(var w in weapons){w.enabled=true;w.damageDealt=(int?)command["Damage"]??0;}
        var body=enemy.GetComponent<Rigidbody2D>();if(body!=null){body.linearVelocity=Vector2.zero;body.bodyType=RigidbodyType2D.Kinematic;}
        // 此夹具只测敌方→友军承伤，排除夹具顺带伤到角色；真实敌人伤角色另做阳性对照。
        foreach(var own in enemy.GetComponentsInChildren<Collider2D>(true))
        foreach(var player in HeroController.SilentInstance.GetComponentsInChildren<Collider2D>(true))Physics2D.IgnoreCollision(own,player,true);
        var follow=enemy.GetComponent<QaHostileContact>()??enemy.AddComponent<QaHostileContact>();
        follow.SubjectRoot=ally;follow.Subject=hurt;follow.Weapon=weapon.GetComponents<Collider2D>().First(c=>c.enabled);follow.Weapon.isTrigger=true;follow.Register();follow.Align();
    }
    /// <summary>只读检查真正参与承伤的缓存碰撞与原生攻击来源，不注入命中。</summary>
    private JObject IncomingContactProbe(JObject command)
    {
        var actor=ActorRoot((int)command["ActorId"]!);var ally=actor.GetComponent<NativeEnemyAlly>()
            ??throw new InvalidOperationException("需要真实友军");
        var cached=AccessTools.Field(typeof(NativeEnemyAlly),"ownColliders")!.GetValue(ally) as List<Collider2D>;
        var all=actor.GetComponentsInChildren<Collider2D>(true);
        var rows=new JArray();
        foreach(var box in all)
        {
            var overlap=new List<Collider2D>();
            if(box.enabled&&box.gameObject.activeInHierarchy)
                box.Overlap(new ContactFilter2D{useTriggers=true,useLayerMask=false},overlap);
            rows.Add(new JObject{["Name"]=Hierarchy(box.transform),["Cached"]=cached?.Contains(box)??false,
                ["Enabled"]=box.enabled,["Active"]=box.gameObject.activeInHierarchy,["Trigger"]=box.isTrigger,
                ["Layer"]=box.gameObject.layer,["X"]=box.bounds.center.x,["Y"]=box.bounds.center.y,
                ["Width"]=box.bounds.size.x,["Height"]=box.bounds.size.y,["Z"]=box.bounds.center.z,["Depth"]=box.bounds.size.z,
                ["TargetZ"]=ally.TargetObject!=null?ally.TargetObject.transform.position.z:0,
                ["Inside3D"]=ally.TargetObject!=null&&box.bounds.Contains(ally.TargetObject.transform.position),
                ["Inside2D"]=ally.TargetObject!=null&&box.OverlapPoint((Vector2)ally.TargetObject.transform.position),["Overlaps"]=new JArray(overlap.Select(c=>
                {
                    var weapon=c.GetComponentInParent<DamageHero>();
                    return new JObject{["Name"]=Hierarchy(c.transform),["Layer"]=c.gameObject.layer,
                        ["Weapon"]=weapon!=null?weapon.name:null,["Damage"]=weapon?.damageDealt,
                        ["Enabled"]=weapon?.enabled,["Active"]=weapon!=null&&weapon.gameObject.activeInHierarchy,
                        ["HeroOwned"]=weapon!=null&&NativeAllyExecution.IsHeroOwned(weapon.gameObject)};
                }))});
        }
        var health=PrimaryHealth(actor);
        return new JObject{["ActorId"]=actor.GetInstanceID(),["Hp"]=health?.hp,["Health"]=health?.name,
            ["Invincible"]=health?.IsInvincible,["CachedCount"]=cached?.Count,["Colliders"]=rows};
    }

    private void SetFixtureWeapon(JObject command)
    {
        var enemy=ActorRoot((int)command["EnemyId"]!);
        var fixture=enemy.GetComponent<QaHostileContact>();if(fixture!=null)fixture.KeepWeaponEnabled=(bool?)command["Enabled"]??true;
        foreach(var weapon in enemy.GetComponentsInChildren<DamageHero>(true))
        {
            weapon.damageDealt=(int?)command["Damage"]??1;weapon.enabled=(bool?)command["Enabled"]??true;
        }
    }
}

/// <summary>只同步敌方原作攻击碰撞体位置，不直接写友军生命值，不替换游戏受击方法。</summary>
internal sealed class QaHostileContact:MonoBehaviour
{
    private static readonly List<QaHostileContact> fixtures=new();
    public bool KeepWeaponEnabled=true;
    public void Register(){if(!fixtures.Contains(this))fixtures.Add(this);}
    public static void MaintainAll()
    {
        fixtures.RemoveAll(f=>f==null||f.SubjectRoot==null);
        foreach(var fixture in fixtures)
        {
            // 强力单位的原作吼叫可能停用其他敌人，测试攻击源须保持可用，不改受测友军。
            if(fixture.KeepWeaponEnabled&&!fixture.gameObject.activeSelf)fixture.gameObject.SetActive(true);
            if(fixture.Weapon!=null)
            {
                fixture.Weapon.enabled=true;
                var damage=fixture.Weapon.GetComponent<DamageHero>();if(damage!=null)damage.enabled=fixture.KeepWeaponEnabled;
            }
        }
    }
    public GameObject? SubjectRoot;
    public Collider2D? Subject;
    public Collider2D? Weapon;
    public void Align()
    {
        if(SubjectRoot!=null&&(Subject==null||!Subject.enabled||!Subject.gameObject.activeInHierarchy||Subject.bounds.size.sqrMagnitude<.0001f))
        {
            Subject=SubjectRoot.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(c=>c.enabled&&c.gameObject.activeInHierarchy&&
                c.bounds.size.sqrMagnitude>.0001f&&c.GetComponentInParent<HealthManager>()!=null&&
                (!c.isTrigger||c.gameObject.layer==11||c.GetComponent<HealthManager>()!=null));
        }
        if(Subject==null||Weapon==null)return;
        transform.position+=Subject.bounds.center-Weapon.bounds.center;
        var body=GetComponent<Rigidbody2D>();if(body!=null){body.position=transform.position;body.linearVelocity=Vector2.zero;}
        Physics2D.SyncTransforms();
    }
    private void FixedUpdate()=>Align();
}
#endif
