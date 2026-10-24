using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiveInteract.Contracts;
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
    private RangeAttacker[] rangeAttackers=Array.Empty<RangeAttacker>();
    private TrackTriggerObjects[] detectors=Array.Empty<TrackTriggerObjects>();
    private readonly List<DamageHero> weapons=new();
    public void AddProjectile(DamageHero weapon,NativeAllyProjectile projectile){if(!weapons.Contains(weapon))weapons.Add(weapon);if(!projectiles.Contains(projectile))projectiles.Add(projectile);}
    public void RemoveProjectile(DamageHero weapon)=>weapons.Remove(weapon);
    private readonly List<FsmGameObject> targets=new();
    private readonly List<(MonoBehaviour Component,FieldInfo Field)> nativeTargets=new();
    private readonly Dictionary<int,float> hits=new();
    private readonly Dictionary<int,float> incoming=new();
    private readonly List<Collider2D> contacts=new();
    private readonly List<Collider2D> touching=new();
    // 逐物理步复用的攻击框与去重集合，避免每步分配。
    private readonly List<Collider2D> boxes=new();
    private readonly HashSet<Collider2D> contactSet=new();
    private float lastFrameSeconds;private int strikeFrame=-1;
    private readonly List<Collider2D> ownColliders=new();
    private readonly RaycastHit2D[] wallContacts = new RaycastHit2D[8];
    private HealthManager? enemy;
    private HealthManager? health;
    private Rigidbody2D? body;
    private float nextTarget;
    private float nextWeaponScan;
    private float nextSelfHit;
    private float offscreenSince=-1;
    private CompanionLocomotion? locomotion;
    private Renderer[] bodyRenderers=Array.Empty<Renderer>();
    /// <summary>仅在原作出招、受击和变身之外应用点名，不截断招式。</summary>
    internal bool CanChangeTarget=>CompanionCombatPolicy.CanChangeTarget(fsms.Where(f=>f!=null&&f.enabled&&f.gameObject.activeInHierarchy).Select(f=>(f.FsmName,f.ActiveStateName)),
        new[]{GetComponentInChildren<tk2dSpriteAnimator>()?.CurrentClip?.name??"",
            GetComponent<ClockworkHatchling>() is ClockworkHatchling clock ? AccessTools.Field(typeof(ClockworkHatchling),"currentState").GetValue(clock)?.ToString()??"" : ""}, GetComponent<OwnedSummon>()?.AssetKey??"");
#if LIVEINTERACT_QA
    internal string? StepProbe => locomotion?.StepProbe;
#endif
    internal string LocomotionMode=>locomotion?.Mode??"Native";
    internal bool NativeFriendlyAi=>GetComponent<ClockworkHatchling>()!=null;
    internal bool IsOnScreen
    {
        get
        {
            var camera=Camera.main;if(camera==null)return true;
            foreach(var renderer in bodyRenderers)
            {
                if(renderer==null||!renderer.enabled||!renderer.gameObject.activeInHierarchy)continue;
                var bounds=renderer.bounds;var low=camera.WorldToViewportPoint(bounds.min);var high=camera.WorldToViewportPoint(bounds.max);
                if(high.z>0&&Mathf.Max(low.x,high.x)>=0&&Mathf.Min(low.x,high.x)<=1&&Mathf.Max(low.y,high.y)>=0&&Mathf.Min(low.y,high.y)<=1)return true;
            }
            var point=camera.WorldToViewportPoint(transform.position);
            return point.z>0&&point.x>=0&&point.x<=1&&point.y>=0&&point.y<=1;
        }
    }
    internal bool SuspendIdleFsm(Fsm fsm)=>locomotion?.Suspends(fsm)==true;
    internal void RecallIfOffscreen(){if(!IsOnScreen)SnapToHero();}
    /// <summary>跟随到位后把目标指回自己：原生移动没有再往前走的方向，小弟就会停在主播身边。</summary>
    public GameObject? TargetObject
    {
        get
        {
            if(HasEnemy)return enemy!.gameObject;
            // 随行层直接读取主播位置；绝不把跟随点喂给原作攻击瞄准。
            return gameObject;
        }
    }
    // 阵营判定要逐级查父物体且每帧被读上千次；同一目标同一帧内不会换阵营，只缓存这一部分，生死仍实时读取。
    private HealthManager? factionChecked;private int factionFrame=-1;private bool factionHostile;
    public bool HasEnemy
    {
        get
        {
            if(enemy==null||enemy.isDead||enemy.hp<=0||!enemy.gameObject.activeInHierarchy)return false;
            if(factionFrame!=Time.frameCount||factionChecked!=enemy)
            {
                factionChecked=enemy;factionFrame=Time.frameCount;
                factionHostile=!NativeAllyExecution.IsFriendly(enemy.gameObject)&&enemy.GetComponentInParent<HeroController>()==null;
            }
            return factionHostile;
        }
    }
    public bool TargetInRange(AlertRange? range)
    {
        if(!HasEnemy)return false;
        // 从场景抽出的FSM可能仍引用原场景的感应区，不能拿它的零bounds判断当前友军。
        if(range!=null&&range.GetComponentInParent<NativeEnemyAlly>()!=this)
            range=GetComponentsInChildren<AlertRange>(true).FirstOrDefault(r=>r.name==range.name);
        var box=range?.GetComponent<Collider2D>();
        if(box==null||!box.enabled||!box.gameObject.activeInHierarchy)return true;
        var target=TargetObject;return target!=null&&box.OverlapPoint((Vector2)target.transform.position);
    }

    /// <summary>只用真实实体形状检查极近的地形侧面；不把警戒框、地面或敌人当墙。</summary>
    internal bool TryGetSolidWallImpact(Vector2 direction, out RaycastHit2D impact)
    {
        var terrainLayer = (int)GlobalEnums.PhysLayers.TERRAIN;
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = 1 << terrainLayer };
        foreach (var collider in ownColliders)
        {
            if (body == null || collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                collider.isTrigger || collider.attachedRigidbody != body) continue;
            var count = collider.Cast(direction, filter, wallContacts, .05f, true);
            for (var i = 0; i < count; i++)
            {
                var hit = wallContacts[i];
                if (hit.collider != null && CompanionTerrainProbe.IsSolidWallImpact(hit.collider.gameObject.layer == terrainLayer,
                    hit.collider.isTrigger, Vector2.Dot(hit.normal, direction), hit.distance))
                { impact = hit; return true; }
            }
        }
        impact = default; return false;
    }

    private bool CanReceiveEnemyContact(Collider2D item)=>item!=null&&
        (!item.isTrigger||item.GetComponent<DamageHero>()!=null||item.gameObject==gameObject||
         item.gameObject.layer==11&&item.GetComponentInParent<HealthManager>()!=null);
    private void Start()
    {
        rangeAttackers=GetComponentsInChildren<RangeAttacker>(true);
        detectors=GetComponentsInChildren<TrackTriggerObjects>(true).Where(d=>d.gameObject.layer==13).ToArray();
        fsms=GetComponentsInChildren<PlayMakerFSM>(true);weapons.AddRange(GetComponentsInChildren<DamageHero>(true));
        foreach(var fsm in fsms)fsm.Fsm.StateChanged+=OnNativeStateChanged;
        // 原作部分Boss的受击框是子物体上的Enemy层触发器，不是根碰撞体。
        ownColliders.AddRange(GetComponentsInChildren<Collider2D>(true).Where(CanReceiveEnemyContact));
        health=GetComponentInChildren<HealthManager>();body=GetComponent<Rigidbody2D>();
        // 友军与主播忽略物理碰撞；原作攻击时序、弹体和地形响应保留。
        var marker=GetComponent<NativeAllyProjectile>()??gameObject.AddComponent<NativeAllyProjectile>();marker.Assign(this,gameObject,true);
        bodyRenderers=GetComponentsInChildren<Renderer>(true).Where(r=>(r is MeshRenderer or SpriteRenderer or SkinnedMeshRenderer)&&r.GetComponent<SummonOutlineVisual>()==null).ToArray();
        locomotion=new CompanionLocomotion(this,body,fsms);
        foreach(var component in GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&c.GetType().Assembly==typeof(HealthManager).Assembly))
        foreach(var field in component is ClockworkHatchling?Array.Empty<FieldInfo>():component.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            var key=field.Name.Replace("_","").ToLowerInvariant();
            if(!field.IsInitOnly&&(field.FieldType==typeof(GameObject)||field.FieldType==typeof(Transform))&&key is "hero" or "player" or "target" or "herotransform" or "herotarget" or "playertransform" or "targettransform")nativeTargets.Add((component,field));
        }
        Retarget();
    }
    /// <summary>在原作退出旧招、尚未执行新状态动作时接收点名，不强设或截断状态。</summary>
    /// <param name="state">原作自然进入的状态。</param>
    private void OnNativeStateChanged(FsmState state)
    {
        // 销毁过程中子状态机仍可能切换状态；本组件已失效时不再访问自身。
        if(this==null||GameActions.Current?.CanAct!=true || NativeFriendlyAi || state?.Fsm==null ||
            !CompanionCombatPolicy.IsRetargetEntryBoundary(GetComponent<OwnedSummon>()?.AssetKey??"",state.Fsm.Name,state.Name))return;
        var focus=CompanionFocus.For(transform.position);
        if(focus==null || focus==enemy)return;
        enemy=focus;
        // 只刷新目标引用；不在状态入场回调里发送感应事件或再次进入状态机。
        var desired=TargetObject;
        foreach(var value in targets)value.Value=desired;
        foreach(var entry in nativeTargets)if(entry.Component!=null)entry.Field.SetValue(entry.Component,entry.Field.FieldType==typeof(Transform)?(object?)desired?.transform:desired);
#if LIVEINTERACT_QA
        QaHarness.RecordActor(gameObject,"natural-focus-boundary:"+state.Fsm.Name+"/"+state.Name);
#endif
    }

    private void Update()
    {
        // FixedUpdate 内 deltaTime 恒为物理步长，帧耗时只能在这里记下。
        lastFrameSeconds=Time.unscaledDeltaTime;
        var hero=HeroController.SilentInstance;if(hero==null)return;
        if(GameActions.Current?.CanAct!=true){offscreenSince=-1;return;}
        var heroPosition=hero.transform.position;
        // 同屏时绝不因距离或两秒超时瞬移；只有持续离开画面才召回。
        var visible=IsOnScreen;
        if(visible)offscreenSince=-1;
        else if(offscreenSince<0)offscreenSince=Time.unscaledTime;
        if(CompanionFollowPolicy.CanRecall(visible,offscreenSince<0?0:Time.unscaledTime-offscreenSince))SnapTo(heroPosition);
        if(Time.unscaledTime>=nextTarget)
        {
            weapons.RemoveAll(w=>w==null);projectiles.RemoveAll(p=>p==null||p.Owner!=this);nextTarget=Time.unscaledTime+.25f;
            var keep=enemy!=null&&CompanionFollowPolicy.KeepTarget(enemy.gameObject.activeInHierarchy&&!enemy.isDead&&enemy.hp>0,
                NativeAllyExecution.IsFriendly(enemy.gameObject),enemy.GetComponentInParent<HeroController>()!=null,
                Vector2.Distance(transform.position,enemy.transform.position),Vector2.Distance(heroPosition,enemy.transform.position));
            var focus=CompanionFocus.For(transform.position);
            if(focus!=null && (!keep || CanChangeTarget))enemy=focus;
            else if(!keep)enemy=GameActions.Current.FindHostile(transform.position,14);
        }
        if(Time.unscaledTime>=nextWeaponScan)
        {
            // 继续收编原作延迟生成的子物体。
            nextWeaponScan=Time.unscaledTime+.5f;
            var friendlyMarker = GetComponent<NativeAllyProjectile>();
            friendlyMarker?.IgnoreHero();
            FriendlyCollisionPairs.Refresh(friendlyMarker);
            // 原生Begin/变身会延迟创建身体受击框；不能只在Start取一次。
            ownColliders.RemoveAll(c=>c==null);
            foreach(var collider in GetComponentsInChildren<Collider2D>(true).Where(CanReceiveEnemyContact))
                if(!ownColliders.Contains(collider))ownColliders.Add(collider);
            foreach(var weapon in GetComponentsInChildren<DamageHero>(true))
            {
                if(weapon==null||weapons.Contains(weapon))continue;
                weapons.Add(weapon);
            }
        }
        locomotion?.Prepare();
        Retarget();
    }
    /// <summary>把友军直接放到角色身侧；重生、传送和场景切换后都走这里，并同步物理位置。</summary>
    public void SnapTo(Vector3 heroPosition)
    {
        var target=heroPosition+new Vector3(-2.2f,Flying?1.6f:.4f,0);
        var rigidbody=body;
        if(rigidbody!=null){rigidbody.linearVelocity=Vector2.zero;rigidbody.position=target;rigidbody.WakeUp();}
        transform.position=target;Physics2D.SyncTransforms();
        offscreenSince=-1;
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
                // 伊莫巴原作接触巡游实际追逐局部Next Point；不改其出生/返场锚点或全局变量。
                if(item.Value==hero?.gameObject || name is "hero" or "player" or "herotarget" or "target" || name.EndsWith("target",StringComparison.Ordinal)
                    || CompanionCombatPolicy.IsVerifiedPursuitVariable(GetComponent<OwnedSummon>()?.AssetKey??"",fsm.FsmName,item.Name??""))targets.Add(item);
            }
        }
        foreach(var value in targets)value.Value=TargetObject;
        var desired=TargetObject;
        // 原生机械友军的target只接收敌人；其Hero引用用于跟随，不能被通用敌怪适配改写。
        var clockwork=GetComponent<ClockworkHatchling>();
        if(clockwork!=null)AccessTools.Field(typeof(ClockworkHatchling),"target").SetValue(clockwork,HasEnemy?enemy!.gameObject:null);
        // 原作范围攻击者的感应条件改为敌人进入，而不是等主播走进攻击范围。
        foreach(var attacker in rangeAttackers)if(attacker!=null)attacker.SetInsideState(HasEnemy);
        foreach(var detector in detectors)
        {
            if(detector==null)continue;var contents=detector.insideObjectsList;if(contents==null)continue;
            var previous=contents.Count>0;var range=detector.GetComponent<Collider2D>();
            var inside=HasEnemy&&desired!=null&&detector.gameObject.activeInHierarchy&&(range==null||range.enabled&&range.OverlapPoint((Vector2)desired.transform.position));
            contents.Clear();if(inside)contents.Add(desired!);
            if(previous!=inside)AccessTools.Method(typeof(TrackTriggerObjects),"CallInsideStateChanged").Invoke(detector,new object[]{inside});
        }
        foreach(var entry in nativeTargets)if(entry.Component!=null)entry.Field.SetValue(entry.Component,entry.Field.FieldType==typeof(Transform)?(object?)desired?.transform:desired);
    }
    /// <summary>只识别该友军自己的FSM局部变量，不写入全局Hero变量。</summary>
    internal bool OwnsTargetVariable(FsmGameObject variable)
    {
        var name=(variable.Name??"").Replace(" ","").ToLowerInvariant();
        return fsms.Any(f=>f!=null&&f.FsmVariables.GameObjectVariables.Any(v=>ReferenceEquals(v,variable))&&
            (name.Contains("target")||name is "hero" or "player"||
             CompanionCombatPolicy.IsVerifiedPursuitVariable(GetComponent<OwnedSummon>()?.AssetKey??"",f.FsmName,variable.Name??"")));
    }

    private void FixedUpdate()
    {
        // 保留原作攻击开关；友敌过滤在受击入口和来源标记执行。

        if(GameActions.Current?.CanAct!=true)return;
        SteerToTarget();
        // 帧率跌破20时Unity每帧追跑十几个物理步，逐步判定攻击会越卡越慢；此时每帧只判一次。
        // 原作攻击框通常持续多个物理步，同一目标本就有0.65秒命中间隔，不会因此漏判。
        if(lastFrameSeconds<=.05f||strikeFrame!=Time.frameCount){strikeFrame=Time.frameCount;StrikeEnemies();}
        TakeEnemyHits();
    }
    /// <summary>按原作实际开启的攻击碰撞向敌人结算，不向主播结算。</summary>
    private void StrikeEnemies()
    {
        for(var weaponIndex=0;weaponIndex<weapons.Count;weaponIndex++)
        {
            var weapon=weapons[weaponIndex];
            if(weapon==null || !weapon.enabled || !weapon.gameObject.activeInHierarchy || weapon.damageDealt<=0)continue;

            weapon.GetComponents(boxes);
            for(var boxIndex=0;boxIndex<boxes.Count;boxIndex++)
            {
                var box=boxes[boxIndex];
                if(!box.enabled)continue;
                var filter=new ContactFilter2D{useTriggers=true,useLayerMask=false};
                contacts.Clear();box.Overlap(filter,contacts);touching.Clear();box.GetContacts(filter,touching);
                if(touching.Count>0)
                {
                    contactSet.Clear();contactSet.UnionWith(contacts);
                    foreach(var item in touching)if(contactSet.Add(item))contacts.Add(item);
                }
                foreach(var contact in contacts)
                {
                    if(contact==null)continue;
                    var owner=NativeCombatContacts.For(contact);
                    var target=owner.Health;
                    if(owner.Friendly || owner.Hero || target==null || target.isDead || target.hp<=0)continue;
                    // 只接受身体本身的碰撞，忽略AI侦测范围等大触发器。
                    if(contact.isTrigger&&contact.gameObject.layer!=11&&contact.gameObject.layer!=target.gameObject.layer&&contact.GetComponent<HealthManager>()==null)continue;
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
                // 只认碰撞体同物体上的真实伤害组件；父级组件不能把子级警戒范围变成攻击。
                var owner=NativeCombatContacts.For(contact);
                if(owner.Friendly || owner.Hero)continue;
                var weapon=owner.Weapon;
                if(weapon==null||!weapon.enabled||!weapon.gameObject.activeInHierarchy||weapon.damageDealt<=0)continue;
                if(weapon.gameObject==gameObject)continue;
                var id=weapon.GetInstanceID();if(incoming.TryGetValue(id,out var next)&&Time.unscaledTime<next)continue;
                incoming[id]=Time.unscaledTime+.8f;
                // 真实敌方武器走原作命中、护甲与阶段判定；IgnoreInvulnerable用于原生闪避流程，并非清空护甲。
                target.Hit(new HitInstance{Source=weapon.gameObject,DamageDealt=Math.Max(1,weapon.damageDealt),AttackType=AttackTypes.Generic,Multiplier=1,MagnitudeMultiplier=1,IsFirstHit=true,IsHeroDamage=false,IgnoreInvulnerable=true});
                if(target.isDead)return;
            }
        }
        if(incoming.Count>64){foreach(var id in incoming.Where(p=>p.Value<Time.unscaledTime).Select(p=>p.Key).ToArray())incoming.Remove(id);}
    }
    /// <summary>只在空闲随行阶段补足移动和原作动画；不重写战斗阶段的刚体速度。</summary>
    private void SteerToTarget()=>locomotion?.Step();
    /// <summary>原作直接扣血招式必须由该友军的实际伤害来源接触敌人身体，不能隔空打当前目标。</summary>
    /// <param name="damage">原作本次直接扣血数值。</param>
    /// <param name="source">原作明确传入的伤害物体，不是默认FSM宿主。</param>
    public void StrikeDirect(int damage,GameObject source)
    {
        // 先确认本次目标、来源阵营与原作伤害窗口，不借用其他单位的碰撞。
        var target=enemy;
        if(damage<=0||target==null||target.isDead||target.hp<=0||source==null||!source.activeInHierarchy)return;
        var sourceOwned=source.GetComponentInParent<NativeEnemyAlly>()==this ||
            source.GetComponentInParent<NativeAllyProjectile>()?.Owner==this;
        if(!sourceOwned)return;
        var id=target.GetInstanceID();
        if(hits.TryGetValue(id,out var next)&&Time.unscaledTime<next)return;
        // 只查原作明确传入的damager，不把子级警戒范围或另一件武器扩成该招式的命中范围。
        var overlaps=new List<Collider2D>();var touchingBody=new List<Collider2D>();
        foreach(var box in source.GetComponents<Collider2D>())
        {
            if(!box.enabled||!box.gameObject.activeInHierarchy)continue;
            var filter=new ContactFilter2D{useTriggers=true,useLayerMask=false};
            overlaps.Clear();box.Overlap(filter,overlaps);touchingBody.Clear();box.GetContacts(filter,touchingBody);
            foreach(var contact in touchingBody)if(!overlaps.Contains(contact))overlaps.Add(contact);
            foreach(var contact in overlaps)
            {
                if(contact==null||!contact.enabled||!contact.gameObject.activeInHierarchy)continue;
                var owner=NativeCombatContacts.For(contact);
                var bodyContact=!owner.Friendly&&!owner.Hero&&owner.Health==target &&
                    (!contact.isTrigger||contact.gameObject.layer==11||contact.gameObject.layer==target.gameObject.layer||contact.GetComponent<HealthManager>()!=null);
                if(!CompanionCombatPolicy.CanRouteDirectDamage(damage,sourceOwned,box.enabled,bodyContact))continue;
                hits[id]=Time.unscaledTime+.65f;
                target.Hit(new HitInstance{Source=source,DamageDealt=Math.Max(1,(int)Math.Ceiling(damage*DamageMultiplier)),AttackType=AttackTypes.Generic,Multiplier=1,MagnitudeMultiplier=1,IsHeroDamage=false});
                return;
            }
        }
    }
    private void OnDestroy()
    {
        // 生成当帧即被清场时 Start 尚未执行，fsms 仍为空。
        if(fsms!=null)foreach(var fsm in fsms)if(fsm!=null&&fsm.Fsm!=null)fsm.Fsm.StateChanged-=OnNativeStateChanged;
        NativeCombatContacts.Clear();locomotion?.Dispose();foreach(var p in projectiles)if(p!=null&&p.Owner==this)p.Disarm();
    }
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
    private static bool Prefix(DamageHero __instance)=>true;
}
/// <summary>友军视野只报告敌方目标；没有敌人时不把主播当作可见目标。</summary>
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
        var ally=__instance.Owner?.GetComponentInParent<NativeEnemyAlly>();if(ally==null)return true;
        var seen=ally.HasEnemy;
        if(__instance is GetCanSeeHero first)
        {
            first.storeResult.Value=seen;
            if(seen&&!first.sendEvent.IsNone&&!string.IsNullOrEmpty(first.sendEvent.Value))first.Fsm.Event(first.eventTarget,first.sendEvent.Value);
            if(!first.everyFrame)first.Finish();
        }
        else if(__instance is GetCanSeeHeroV2 second)
        {
            second.storeResult.Value=seen;var ev=seen?second.inRangeEvent:second.outOfRangeEvent;
            if(!ev.IsNone&&!string.IsNullOrEmpty(ev.Value))second.Fsm.Event(second.eventTarget,ev.Value);
            if(!second.everyFrame)second.Finish();
        }
        return false;
    }
}
/// <summary>保留原作朝向与站位算法，但读取友军的实际敌方目标。</summary>
[HarmonyPatch]
internal static class FriendlyHeroAttachHook
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MatchHeroFacing),"DoMatch");
        yield return AccessTools.Method(typeof(PositionToHeroClamped),"Update");
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>NativeAllyExecution.RetargetInstructions(instructions);
}
/// <summary>友军可以承受敌人伤害；主播与其他友军的命中仍然不计，避免自己人误杀。</summary>
[HarmonyPatch(typeof(HealthManager),nameof(HealthManager.Hit))]
internal static class FriendlyNativeHealthHook
{
    private static bool Prefix(HealthManager __instance,HitInstance __0)
    {
        if(__instance.GetComponentInParent<OwnedSummon>()==null)return true;
        if(__instance.GetComponentInParent<OwnedCompanion>()!=null&&NativeAllyExecution.IsHeroHit(__0))return false;
        // 护甲、免疫和特殊死亡属于原作阶段状态；普通命中不得抹掉这些标志。
        return true;
    }
}

/// <summary>原作 FSM 的直接伤害改为对真实敌人结算，不能借旁路去扣主播血。</summary>
[HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.DamageHeroDirectly),nameof(HutongGames.PlayMaker.Actions.DamageHeroDirectly.OnEnter))]
internal static class FriendlyDirectDamageHook
{
    private static bool Prefix(HutongGames.PlayMaker.Actions.DamageHeroDirectly __instance)
    {
        var ally=__instance.Owner?.GetComponentInParent<NativeEnemyAlly>()??NativeAllyExecution.Current;if(ally==null)return true;
        // 与原作OnEnter读取同一damager；FSM宿主不一定就是实际武器。
        var source=__instance.Fsm.GetOwnerDefaultTarget(__instance.damager);
        if(source!=null)ally.StrikeDirect(__instance.damageAmount.Value,source);
        __instance.Finish();return false;
    }
}

/// <summary>同一物理步共享碰撞体归属，避免密集友军互相反复扫描整个父级链。</summary>
internal static class NativeCombatContacts
{
    // 仅保留当前物理步的对象引用；下一步重新识别转换、池化和阵营变化。
    private static readonly Dictionary<Collider2D, Owner> Cache = new();
    private static float fixedTime = float.NaN;

    /// <summary>碰撞体的稳定归属；生命值、伤害开关仍在实际使用时读取。</summary>
    internal readonly struct Owner
    {
        internal readonly bool Friendly;
        internal readonly bool Hero;
        internal readonly HealthManager? Health;
        internal readonly DamageHero? Weapon;

        /// <summary>只识别碰撞体同物体的伤害组件，不把父级警戒框误当攻击。</summary>
        internal Owner(Collider2D collider)
        {
            Friendly = NativeAllyExecution.IsFriendly(collider.gameObject);
            Hero = collider.GetComponentInParent<HeroController>() != null;
            Health = Friendly || Hero ? null : collider.GetComponentInParent<HealthManager>();
            Weapon = Friendly || Hero ? null : collider.GetComponent<DamageHero>();
        }
    }

    /// <summary>本体清理时也释放最后一个物理步的托管引用。</summary>
    internal static void Clear() { Cache.Clear(); fixedTime = float.NaN; }

    /// <summary>返回当前物理步的归属；不缓存生命、激活状态或碰撞结果。</summary>
    internal static Owner For(Collider2D collider)
    {
        if (fixedTime != Time.fixedTime)
        {
            Cache.Clear(); fixedTime = Time.fixedTime;
        }
        if (!Cache.TryGetValue(collider, out var owner))
        {
            owner = new Owner(collider); Cache.Add(collider, owner);
        }
        return owner;
    }
}
