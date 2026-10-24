using System;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using LiveInteract.Contracts;
using UnityEngine;

namespace LiveInteract.Silksong;

/// <summary>按原作行走、地面爬行和飞行空闲动作接管随行；进入原作感知区即交还战斗。</summary>
internal sealed class CompanionLocomotion : IDisposable
{
    // 原作组件和当前接管的移动状态。
    private readonly NativeEnemyAlly owner;
    // 精确匹配已核对的特殊中性状态，不接管其他单位的同名招式。
    private readonly string asset;
    private readonly Rigidbody2D? body;
    private readonly PlayMakerFSM[] fsms;
    private readonly tk2dSpriteAnimator? animator;
    private readonly Crawler? crawler;
    private readonly Walker? walker;
    // 新版原作行走组件具有自己的协程、朝向约定和动画参数。
    private readonly WalkerV2? walkerV2;
    private readonly AmbientSway? ambientSway;
    private readonly DamageHero? bodyDamager;
    private readonly DamageHero? rootBodyDamager;
    // 潘德拉戈原作武器和翻转节点都位于Sprite子物体，不能按根节点朝向重复翻转。
    private readonly DamageHero? patrolBodyDamager;
    private readonly Transform? patrolFacing;
    private readonly FsmStateAction[] nativeActions;
    // 缓存已核对的敌情开关，不逐帧扫描全部FSM变量。
    private readonly FsmBool[] enemyStartles;
    private bool hasMoveClip;
    private Fsm? suspendedFsm;
    private FsmState? suspendedState;
    private WalkLeftRight? walk;
    // 部分原作巡逻只在入态时设置速度，没有Walker组件。
    private SetVelocityByScale? scaledWalk;
    private bool componentWalk;
    private bool componentWalkV2;
    private bool swayPaused, swayWasActive;
    private bool crawling, flying, hovering, moving, spriteFacesLeft;
    private float speed;
    private string moveClip = "";
    private string idleClip = "Idle";
    private string? displayedClip;
    private readonly RaycastHit2D[] terrainHits = new RaycastHit2D[12];
    private Collider2D[] physicalColliders = Array.Empty<Collider2D>();
    private int terrainMask;
    private float detourUntil, detourDirection, groundStuckSeconds;


#if LIVEINTERACT_QA
    internal string StepProbe { get; private set; } = "not stepping";
#endif
    internal bool IsFollowing => suspendedFsm != null && moving;
    internal bool IsControllingIdle => suspendedFsm != null;
    internal string Mode => suspendedFsm == null ? "Native" : (owner.HasEnemy ? "Approach:" : "Follow:") + (flying ? "Fly" : crawling ? "Crawler" : hasMoveClip ? "Walk" : "AwaitAdapter");

    /// <summary>绑定当前召唤副本的原作移动组件。</summary>
    /// <param name="owner">友军副本。</param>
    /// <param name="body">其物理刚体。</param>
    /// <param name="fsms">其原作状态机。</param>
    internal CompanionLocomotion(NativeEnemyAlly owner, Rigidbody2D? body, PlayMakerFSM[] fsms)
    {
        this.owner = owner;
        this.body = body;
        this.fsms = fsms;
        asset = owner.GetComponent<OwnedSummon>()?.AssetKey ?? "";
        enemyStartles = fsms.SelectMany(f => f.FsmVariables.BoolVariables.Where(v =>
            CompanionCombatPolicy.ShouldClearCompanionStartle(asset, f.FsmName, v.Name, false))).ToArray();
        animator = owner.GetComponentInChildren<tk2dSpriteAnimator>();
        crawler = owner.GetComponent<Crawler>();
        walker = owner.GetComponent<Walker>();
        walkerV2 = owner.GetComponent<WalkerV2>();
        ambientSway = owner.GetComponent<AmbientSway>();
        bodyDamager = owner.GetComponentsInChildren<DamageHero>(true).FirstOrDefault(w => w.name == "Body Damager");
        rootBodyDamager = owner.GetComponent<DamageHero>();
        if (asset == "spawn.dragonfly-large")
        {
            patrolBodyDamager = owner.GetComponentsInChildren<DamageHero>(true).FirstOrDefault(w => w.name == "Sprite");
            patrolFacing = fsms.FirstOrDefault(f => f.gameObject == owner.gameObject && f.FsmName == "Control")?.FsmVariables.FindFsmGameObject("Sprite")?.Value?.transform;
        }
        physicalColliders = owner.GetComponentsInChildren<Collider2D>(true);
        terrainMask = LayerMask.GetMask("Terrain");
        nativeActions = fsms.SelectMany(f => f.FsmStates).SelectMany(s => s.Actions ?? Array.Empty<FsmStateAction>()).ToArray();
    }

    /// <summary>只接管有明确原作移动动作的空闲状态，不按 Boss 身份排除。</summary>
    internal void Prepare()
    {
        // 原作出生标记会在Chase Tele?直接触发警觉传送；无敌人时沿自然FINISHED分支回到行走。
        if (!owner.HasEnemy) foreach (var flag in enemyStartles) flag.Value = false;
        if (suspendedFsm != null && (suspendedFsm.ActiveState != suspendedState || owner.HasEnemy && InNativeRange())) Release();
        if (suspendedFsm != null || body == null || owner.NativeFriendlyAi) return;
        foreach (var fsm in fsms)
        {
            // 塞斯辅助武器的Idle不能抢占本体出招；只允许原作根Control接管随行。
            if (!CompanionCombatPolicy.CanUseLocomotionController(asset, fsm.FsmName, fsm.gameObject == owner.gameObject)) continue;
            // 已核对的休眠型通过自己的WAKE链苏醒，保留原作出生/起身动画，不强塞攻击状态。
            var state = fsm.Fsm.ActiveState;
            if (!fsm.enabled || state == null) continue;
            var hero = HeroController.SilentInstance;
            var heroDistance = hero != null ? Math.Abs(owner.transform.position.x - hero.transform.position.x) : 0;
            // 尖角碎裂者的隐枝入口使用AWAKE而不是WAKE，只认原Hidden→Burst Antic出场链。
            var wakeEvent = asset == "spawn.stick-insect-charger" && fsm.FsmName == "Behaviour Base" && state.Name == "Hidden"
                && state.Transitions.Any(t => t.EventName == "AWAKE" && t.ToState == "Burst Antic") ? "AWAKE" : "WAKE";
            if (CompanionCombatPolicy.ShouldWakeForCompanion(state.Name,
                state.Transitions.Any(t => t.EventName == wakeEvent), owner.HasEnemy, heroDistance, asset, fsm.FsmName)
                || CompanionCombatPolicy.ShouldWakeSkullCompanion(asset, fsm.FsmName, state.Name,
                    state.Transitions.Any(t => t.EventName == "WAKE" && t.ToState == "Wake Antic")))
            {
                fsm.SendEvent(wakeEvent);
                continue;
            }
            // 已核对的场景出场入口沿自身BATTLE START链恢复；不模拟波次结束，不强设攻击态。
            if (CompanionCombatPolicy.ShouldStartCompanionEntrance(asset, fsm.FsmName, state.Name,
                state.Transitions.Any(t => t.EventName == "BATTLE START" && (asset == "spawn.bone-hunter-chief" && t.ToState == "Battle Wait"
                    || asset == "spawn.coral-conch-driller-giant" && t.ToState == "Start Pause"))))
            {
                fsm.SendEvent("BATTLE START");
                continue;
            }
            // 部分休眠单位使用 ALERT 而非 WAKE；只发送状态图已有的自然起身事件。
            if (CompanionCombatPolicy.ShouldAlertForCompanion(state.Name,
                state.Transitions.Any(t => t.EventName == "ALERT"), owner.HasEnemy, heroDistance, asset, fsm.FsmName))
            {
                fsm.SendEvent("ALERT");
                continue;
            }
            if (state.Name == "Rest Wall" && state.Transitions.Any(t => t.EventName == "UNDERSTORE WAKE"))
            {
                fsm.SendEvent("UNDERSTORE WAKE");
                continue;
            }
            // 顶挂掷面虫保留原作下落和刹停链，不能把挂顶态直接改成漂浮或攻击态。
            if (CompanionCombatPolicy.ShouldLaunchCeilingFlyer(state.Name,
                state.Transitions.Any(t => t.EventName == "ALERT"),
                nativeActions.Any(a => a is DistanceFly or DistanceFlyV2 or DistanceFlySmooth), owner.HasEnemy, heroDistance))
            {
                fsm.SendEvent("ALERT");
                continue;
            }
            // 蛋形幼蝇缺少原场景广播时，沿其原作孵化链出生，不能直接强设 Fly 或攻击状态。
            if (CompanionCombatPolicy.ShouldHatchForCompanion(fsm.FsmName, state.Name,
                state.Transitions.Any(t => t.EventName == "BATTLE START" && t.ToState == "Hatch Pause"),
                owner.HasEnemy, heroDistance))
            {
                fsm.SendEvent("BATTLE START");
                continue;
            }
            // 采集飞虫的ALERT会经过FireAtTarget；随行只暂挂停靠，不能为了离墙向自己开火。
            var perchedFlight = body.gravityScale == 0 && CompanionCombatPolicy.IsNeutralPerchedFlight(
                asset, fsm.FsmName, state.Name, nativeActions.Any(IsFlightAction));
            // 原场景Next Node补间与友军速度互相覆盖；只接管已核对的非招式巡航态。
            var patrolTween = state.Actions?.OfType<iTweenMoveTo>().FirstOrDefault(a => a.Enabled && a.stopOnExit.Value && a.speed.Value > 0);
            var tweenFlight = body.gravityScale == 0 && patrolFacing != null && animator?.GetClipByName("Fly") != null
                && CompanionCombatPolicy.UsesContinuousTweenFlight(asset, fsm.FsmName, state.Name, patrolTween != null, HasOriginalPatrolBodyWeapon);
            var flightReady = tweenFlight || perchedFlight || CompanionCombatPolicy.IsNeutralNativeFlight(fsm.FsmName, state.Name,
                body.gravityScale == 0 && state.Actions?.Any(IsFlightAction) == true, asset)
                || CompanionCombatPolicy.IsNeutralChaseFlight(fsm.FsmName, state.Name,
                    body.gravityScale == 0 && state.Actions?.Any(a => a is DistanceFly or DistanceFlyV2 or DistanceFlySmooth or DistanceFlyHorizontal) == true, asset);
            var hoverReady = CompanionCombatPolicy.UsesStationaryHoverFollow(asset, fsm.FsmName, state.Name)
                && animator?.GetClipByName("Idle") != null;
            // 拟草虫原作只有身体接触伤害，感知后逃跑；接敌移动继续沿原Crawl/Run和身体武器，不制造出招。
            var contactGround = CompanionCombatPolicy.UsesContinuousContactGround(asset, fsm.FsmName, state.Name, HasOriginalFloorBodyWeapon);
            if (!CompanionCombatPolicy.IsIdleLocomotion(state.Name) && !flightReady && !hoverReady && !contactGround) continue;
            var nativeWalk = state.Actions?.OfType<WalkLeftRight>().FirstOrDefault();
            var profileWalk = nativeWalk ?? nativeActions.OfType<WalkLeftRight>().FirstOrDefault();
            var nativeScaledWalk = body.gravityScale > 0 ? state.Actions?.OfType<SetVelocityByScale>().FirstOrDefault(a => Mathf.Abs(a.speed.Value) > .01f) : null;
            var nativeWalker = CompanionCombatPolicy.CanUseWalkerLocomotion(state.Name,
                walker != null && walker.enabled, state.Actions?.Any(a => a is StartWalker) == true);
            var nativeWalkerV2 = CompanionCombatPolicy.CanUseWalkerLocomotion(state.Name,
                walkerV2 != null && walkerV2.enabled, state.Actions?.Any(a => a is StartWalkerV2) == true);
            var flight = nativeActions.FirstOrDefault(IsFlightAction);
            var nativeCrawler = CompanionCombatPolicy.CanUseCrawlerLocomotion(state.Name,
                crawler != null && crawler.Type == Crawler.CrawlerTypes.Floor,
                crawler?.IsCrawling == true, state.Actions?.Any(a => a is StartCrawler) == true) || contactGround;
            // 只在主AI或明确的原生移动状态接管，不把辅助/阶段FSM的Idle当作本体行走。
            if (nativeWalk == null && !nativeWalker && !nativeWalkerV2 && !nativeCrawler && !flightReady && fsm.FsmName is not ("Control" or "Behaviour" or "Attack Enemies")) continue;

            suspendedFsm = fsm.Fsm;
            suspendedState = state;
            walk = nativeWalk;
            scaledWalk = nativeScaledWalk;
            componentWalk = nativeWalker;
            componentWalkV2 = nativeWalkerV2;
            crawling = nativeCrawler;
            hovering = hoverReady;
            flying = tweenFlight || hovering || CompanionCombatPolicy.UsesFlightLocomotion(nativeWalk != null || nativeWalker || nativeWalkerV2 || nativeCrawler || nativeScaledWalk != null, state.Actions?.Any(IsFlightAction) == true, profileWalk != null, flight != null);
            speed = tweenFlight ? patrolTween!.speed.Value : hovering ? 1.5f : nativeWalkerV2 ? Mathf.Abs(walkerV2!.WalkSpeed) : nativeWalker ? Mathf.Max(Mathf.Abs(walker!.walkSpeedL), Mathf.Abs(walker.walkSpeedR)) : profileWalk != null ? Mathf.Abs(profileWalk.walkSpeed) : nativeScaledWalk != null ? Mathf.Abs(nativeScaledWalk.speed.Value) : nativeCrawler ? Mathf.Abs(crawler!.Speed) : Mathf.Max(2, (flight == null ? 3 : (flight.GetType().GetField("speedMax", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(flight) as FsmFloat)?.Value ?? 3));
            var fallbackFacesRight = state.Actions?.OfType<FaceDirection>().FirstOrDefault()?.spriteFacesRight.Value
                ?? state.Actions?.OfType<FaceObjectV2>().FirstOrDefault()?.spriteFacesRight.Value
                ?? state.Actions?.OfType<FaceObject>().FirstOrDefault()?.spriteFacesRight.Value
                ?? nativeActions.OfType<FaceObjectV2>().FirstOrDefault()?.spriteFacesRight.Value
                ?? nativeActions.OfType<FaceDirection>().FirstOrDefault()?.spriteFacesRight.Value ?? false;
            spriteFacesLeft = nativeWalkerV2 ? walkerV2!.RightDirection < 0
                : nativeWalker ? (float)AccessTools.Field(typeof(Walker), "rightScale").GetValue(walker) < 0
                : CompanionFollowPolicy.SpriteFacesLeft(profileWalk?.spriteFacesLeft,
                    crawler != null && crawler.Type == Crawler.CrawlerTypes.Floor
                        ? (bool?)AccessTools.Field(typeof(Crawler), "spriteFacesRight").GetValue(crawler) : null,
                    fallbackFacesRight);
            idleClip = nativeWalkerV2 ? walkerV2!.IdleAnim : nativeWalker ? (string)AccessTools.Field(typeof(Walker), "idleClip").GetValue(walker) : "Idle";
            var knownClip = nativeWalkerV2 ? walkerV2!.WalkAnim : nativeWalker ? (string)AccessTools.Field(typeof(Walker), "walkClip").GetValue(walker) : profileWalk?.walkAnimName.Value ?? (nativeScaledWalk != null ? animator?.CurrentClip?.name ?? "Walk" : nativeCrawler ? crawler!.crawlAnimName : flying ? "Fly" : "Walk");
            // 没有原生步行动作的特殊单位以原作Idle姿态低速悬浮，不用冲撞动画冒充走路。
            if (hovering) knownClip = "Idle";
            // 肥游鱼的转向片段不是游动循环；使用已核对且实际存在的原作Fat Swim。
            if(asset == "spawn.coral-swimmer-fat" && flying && animator?.GetClipByName("Fat Swim") != null)knownClip="Fat Swim";
            // 旋角凯虫使用原作Swim循环，TurnToSwim只是转向片段。
            if(asset == "spawn.poke-swimmer" && flying && animator?.GetClipByName("Swim") != null)knownClip="Swim";
            // 原节点间的TurnToFly只负责转身，随行保持原作Fly循环。
            if (tweenFlight) knownClip = "Fly";
            // Wall Collect是原作停靠采集姿势，空中随行使用已核对的Idle拍翼循环。
            if(perchedFlight && flying && animator?.GetClipByName("Idle") != null)knownClip="Idle";
            moveClip = animator?.GetClipByName(knownClip) != null ? knownClip : animator?.GetClipByName("Run") != null ? "Run" : state.Actions?.OfType<Tk2dPlayAnimation>().FirstOrDefault()?.clipName.Value ?? animator?.CurrentClip?.name ?? "Idle";
            hasMoveClip = flying || nativeCrawler || nativeScaledWalk != null && animator?.CurrentClip != null || animator?.GetClipByName(knownClip) != null || animator?.GetClipByName("Run") != null;
            // Pause 也可能只是出生前的 0.1 秒等待；没有移动配置时继续运行原作状态机。
            if (!CompanionCombatPolicy.CanSuspendIdle(hasMoveClip, state.Transitions.Select(t => t.ToState))) { Release(false); continue; }
            if (owner.HasEnemy && InNativeRange()) { Release(false); continue; }
            // 停止原移动补间及完成事件，保持当前坐标；不发送FINISHED或进入攻击状态。
            if (tweenFlight) patrolTween!.OnExit();
            nativeWalk?.OnExit(); // 原作 Walk/Turn 协程也要停止，单跳过 FSM.Update 不会停止它。
            if (nativeCrawler) crawler!.StopCrawling();
            if (nativeWalker) walker!.Stop(Walker.StopReasons.Controlled);
            if (nativeWalkerV2) walkerV2!.StopWalking();
            // 根节点的环境摆动会每帧写回出生锚点；随行时暂停，装饰子节点不受影响。
            if (ambientSway != null)
            {
                swayWasActive = (bool)AccessTools.Field(typeof(AmbientSway), "active").GetValue(ambientSway);
                swayPaused = true;
                ambientSway.StopSway();
            }
            owner.Flying = flying;
            moving = false;
            displayedClip = null;
            break;
        }
    }

    private bool IsFlightAction(FsmStateAction action) => asset == "spawn.bone-hunter-chief" && action is DistanceFlyV3
        || action is IdleBuzz or IdleBuzzV2 or IdleBuzzV3 or IdleBuzzV4 or DistanceFly or DistanceFlyV2 or DistanceFlySmooth or DistanceFlyHorizontal or ChaseObjectVertical
        || body?.gravityScale == 0 && action is ChaseObject or ChaseObjectV2 or ChaseObjectV3 or ChaseObjectSpread or SetVelocityAsAngle or AccelerateToX;

    /// <summary>只有当前空闲移动状态被暂挂，不阻止事件、受击和死亡切换。</summary>
    /// <param name="fsm">待更新状态机。</param>
    /// <returns>是否由随行层暂时更新移动。</returns>
    internal bool Suspends(Fsm fsm)
    {
        // 焚身信徒的跌落检测会触发原作传送；随行时不能用它代替地面接近。
        if (CompanionCombatPolicy.ShouldPauseFollowFallTeleport(fsm.Name, fsm.ActiveState?.Name ?? "", owner.HasEnemy)) return true;
        if (suspendedFsm == null || suspendedFsm.ActiveState != suspendedState ||
            !CompanionCombatPolicy.CanSteerIdle(owner.HasEnemy, InNativeRange(), owner.NativeFriendlyAi, true)) return false;
        // 蝙蝠由主FSM水平移动、Flap辅助FSM上下拍翼；随行不能被旧拍翼速度再次拉走。
        return suspendedFsm == fsm || flying && suspendedState?.Name == "Flapping" &&
            fsm.Name == "Flap" && fsm.ActiveState?.Name is "Flap Up" or "Flap Down";
    }

    /// <summary>沿物理平面接近主播或尚在原作感知区外的敌人。</summary>
    internal void Step()
    {
        if (suspendedFsm == null || body == null || !Suspends(suspendedFsm)) return;
        var hero = HeroController.SilentInstance;
        if (hero == null) return;
        var target = owner.HasEnemy ? owner.TargetObject!.transform.position : hero.transform.position + new Vector3(0, flying ? 1.2f : 0, 0);
        var delta = (Vector2)(target - owner.transform.position);
        moving = hasMoveClip && (owner.HasEnemy || CompanionFollowPolicy.ShouldFollow(flying ? delta.magnitude : Math.Abs(delta.x), moving));
        var velocity = body.linearVelocity;
        groundStuckSeconds = !flying && moving && Mathf.Abs(velocity.x) < speed * .2f ? groundStuckSeconds + Time.fixedDeltaTime : 0;
        if (flying)
        {
            velocity = moving ? FlightDirection(delta) * speed : Vector2.zero;
            // 只抵消本物理步的重力，不修改原生gravityScale；交还攻击时没有重力恢复竞态。
            if (hovering) velocity -= Physics2D.gravity * (body.gravityScale * Time.fixedDeltaTime);
        }
        else
        {
            velocity.x = moving ? Mathf.Sign(delta.x) * speed : 0;
            if (moving) WalkUpSmallStep(Mathf.Sign(delta.x), ref velocity);
        }
        body.linearVelocity = velocity;
        if (moving && Math.Abs(delta.x) > .1f)
        {
            // 潘德拉戈原FaceObjectV3只翻Sprite；其余单位仍沿用各自根节点约定。
            var facing = patrolFacing != null && asset == "spawn.dragonfly-large" ? patrolFacing : owner.transform;
            var scale = facing.localScale;
            scale.x = Mathf.Abs(scale.x) * (delta.x < 0 == spriteFacesLeft ? 1 : -1);
            facing.localScale = scale;
        }
        PlayOriginal(moving || flying ? moveClip : idleClip);
    }

    /// <summary>只绕开真实地形；通过物理速度贴边飞行，不瞬移或关闭碰撞。</summary>
    private Vector2 FlightDirection(Vector2 delta)
    {
        var direction = delta.normalized;
        if (Time.time < detourUntil && Mathf.Sign(delta.x) == detourDirection)
            direction = new Vector2(direction.x, Mathf.Max(.7f, direction.y)).normalized;
        if (!TerrainBlocked(direction, .45f, out _)) return direction;
        var up = new Vector2(Mathf.Sign(delta.x) * .15f, 1).normalized;
        if (!TerrainBlocked(up, .45f, out _))
        {
            detourUntil = Time.time + .65f; detourDirection = Mathf.Sign(delta.x);
            return up;
        }
        var down = new Vector2(Mathf.Sign(delta.x) * .15f, -1).normalized;
        return TerrainBlocked(down, .45f, out _) ? Vector2.zero : down;
    }

    private bool TerrainBlocked(Vector2 direction, float distance, out RaycastHit2D obstruction)
    {
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = terrainMask };
        // Rigidbody.Cast也会扫到本体的大型警戒触发框；只能用真实实体碰撞形状。
        foreach (var collider in physicalColliders)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                collider.attachedRigidbody != body || collider.bounds.size.sqrMagnitude < .001f) continue;
            var count = collider.Cast(direction, filter, terrainHits, distance, true);
            for (var i = 0; i < count; i++)
                if (terrainHits[i].collider != null && Vector2.Dot(terrainHits[i].normal, direction) < -.15f)
                { obstruction = terrainHits[i]; return true; }
        }
        obstruction = default; return false;
    }

    /// <summary>跨小台阶而不悬浮上高平台；保留重力和真实碰撞。</summary>
    private void WalkUpSmallStep(float direction, ref Vector2 velocity)
    {
        // 接触偏移处的拼接边可能不返回侧向法线；只在真实停滞后再检查前脚高度。
        if (velocity.y > 1 || !TerrainBlocked(new Vector2(direction, 0), .2f, out _) && groundStuckSeconds < .2f) return;
        Collider2D? feet = null;
        foreach (var collider in physicalColliders)
            if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger &&
                collider.attachedRigidbody == body && collider.bounds.size.y > .05f && (feet == null || collider.bounds.min.y < feet.bounds.min.y)) feet = collider;
        if (feet == null) return;
        var bounds = feet.bounds;
        // 支撑点可在前脚下；台阶交接时中心点还在低一级，不能误判为空中。
        var grounded = Physics2D.Raycast(new Vector2(bounds.center.x, bounds.min.y + .03f), Vector2.down, .2f, terrainMask).collider != null ||
            Physics2D.Raycast(new Vector2(bounds.center.x + direction * (bounds.extents.x - .05f), bounds.min.y + .03f), Vector2.down, .2f, terrainMask).collider != null;
        var ahead = Physics2D.Raycast(new Vector2(bounds.center.x + direction * (bounds.extents.x + .15f), bounds.min.y + .5f), Vector2.down, .6f, terrainMask);
        var rise = ahead.collider == null ? -1 : ahead.point.y - bounds.min.y;
        var headroom = !TerrainBlocked(Vector2.up, Mathf.Max(0, rise) + .05f, out var ceiling);
#if LIVEINTERACT_QA
        StepProbe = $"feet={feet.name} body={body!.GetInstanceID()} rise={rise} grounded={grounded} headroom={headroom} ceiling={ceiling.collider?.name} normal={ceiling.normal} distance={ceiling.distance} bottom={bounds.min.y} ahead={ahead.collider?.name} groundX={bounds.center.x + direction * (bounds.extents.x - .05f)}";
#endif
        if (CompanionFollowPolicy.CanWalkUpStep(rise, grounded, headroom))
            velocity.y = Mathf.Max(velocity.y, Mathf.Min(6, rise * 12 + 3));
    }

    private bool HasOriginalFloorBodyWeapon => crawler != null && crawler.Type == Crawler.CrawlerTypes.Floor
        && rootBodyDamager != null && rootBodyDamager.enabled && rootBodyDamager.gameObject.activeInHierarchy && rootBodyDamager.damageDealt > 0;

    // 只认原作Sprite身体武器的实际启用状态，不为无攻击单位补碰撞伤害。
    private bool HasOriginalPatrolBodyWeapon => patrolBodyDamager != null && patrolBodyDamager.enabled
        && patrolBodyDamager.gameObject.activeInHierarchy && patrolBodyDamager.damageDealt > 0;

    /// <summary>查询当前原生感知条件；不扩大攻击碰撞或伪造命中。</summary>
    /// <returns>是否可以把移动交还原作战斗。</returns>
    private bool InNativeRange()
    {
        if (!owner.HasEnemy) return false;
        // 原节点巡航没有另一个出招态；持续物理接敌，伤害仍由原身体武器真实碰撞结算。
        if (CompanionCombatPolicy.UsesContinuousTweenFlight(asset, suspendedFsm?.Name ?? "", suspendedState?.Name ?? "",
            suspendedState?.Actions?.Any(a => a is iTweenMoveTo) == true, HasOriginalPatrolBodyWeapon)) return false;
        // 已核对的接触型地面单位不能在警戒距离就交回逃跑，须沿原身体碰撞真实接敌。
        if (CompanionCombatPolicy.UsesContinuousContactGround(asset, suspendedFsm?.Name ?? "", suspendedState?.Name ?? "", HasOriginalFloorBodyWeapon)) return false;
        // 幼蝇没有独立近战出招；不能在四单位外交还随机游飞，须让原生身体碰撞真正接敌。
        if (CompanionCombatPolicy.UsesContinuousContactFlight(suspendedFsm?.Name ?? "", suspendedState?.Name ?? "",
            bodyDamager != null && bodyDamager.enabled && bodyDamager.gameObject.activeInHierarchy && bodyDamager.damageDealt > 0))
        {
            return false;
        }
        var checks = suspendedState?.Actions?.OfType<CheckAlertRangeByName>().ToArray() ?? Array.Empty<CheckAlertRangeByName>();
        var ranges = owner.GetComponentsInChildren<AlertRange>(true);
        // 帷虫Flapping同时查警戒/近距/攻击区；只到真实攻击区才交还原作冲刺链。
        var preferredRange = CompanionCombatPolicy.PreferredCombatApproachRange(asset, suspendedFsm?.Name ?? "", suspendedState?.Name ?? "");
        if (preferredRange != null && ranges.FirstOrDefault(r => r.name == preferredRange) is AlertRange attackRange)
            return owner.TargetInRange(attackRange);
        foreach (var check in checks)
        {
            var range = ranges.FirstOrDefault(r => r.name == check.alertRangeName);
            if (range != null && owner.TargetInRange(range)) return true;
        }
        var directChecks = suspendedState?.Actions?.OfType<CheckAlertRange>().ToArray() ?? Array.Empty<CheckAlertRange>();
        foreach (var check in directChecks)
            if (check.alertRange.Value is AlertRange range && owner.TargetInRange(range)) return true;
        if (checks.Length + directChecks.Length > 0 && ranges.Length > 0) return false;
        return Vector2.Distance(owner.transform.position, owner.TargetObject!.transform.position) <= (flying ? 4 : 1.5f);
    }

    /// <summary>使用原作已有动画，不生成替代攻击动画。</summary>
    /// <param name="clip">原作动画名。</param>
    private void PlayOriginal(string clip)
    {
        if (animator == null || animator.GetClipByName(clip) == null) return;
        if (displayedClip != clip || !animator.IsPlaying(clip)) { animator.Play(clip); displayedClip = clip; }
    }

    /// <summary>恢复原作移动，不覆盖已经切换到攻击状态的速度。</summary>
    /// <param name="resumeNative">是否恢复仍处于相同状态的原生动作。</param>
    private void Release(bool resumeNative = true)
    {
        var fsm = suspendedFsm;
        var state = suspendedState;
        var action = walk;
        var scaledAction = scaledWalk;
        var resumeCrawler = crawling;
        var resumeWalker = componentWalk;
        var resumeWalkerV2 = componentWalkV2;
        var restoreSway = swayWasActive;
        // 攻击或事件已经换状态时只重记当前锚点，不覆盖新状态对摆动的启停决定。
        if (swayPaused && ambientSway != null && owner != null && owner.gameObject.activeInHierarchy)
        {
            ambientSway.RecordInitialPosition();
        }
        swayPaused = false; swayWasActive = false;
        suspendedFsm = null; suspendedState = null; walk = null; scaledWalk = null; componentWalk = false; componentWalkV2 = false;
        moving = false; hovering = false; displayedClip = null;
        if (!resumeNative || owner == null || !owner.gameObject.activeInHierarchy || fsm?.ActiveState != state) return;
        if (body != null)
        {
            var velocity = body.linearVelocity; velocity.x = 0; body.linearVelocity = velocity;
        }
        if (restoreSway) ambientSway?.ResumeSway();
        action?.OnEnter();
        scaledAction?.OnEnter();
        // 原生游荡动作的锚点仍在出生地；恢复同一空闲状态时从当前位置重新开始。
        foreach (var flight in state?.Actions?.Where(a => a is IdleBuzz or IdleBuzzV2 or IdleBuzzV3 or IdleBuzzV4)
            ?? Array.Empty<FsmStateAction>())
        {
            flight.OnEnter();
        }
        // StartWalker通常在前置状态已经Finish；只恢复组件，不重新进入非当前FSM动作。
        if (resumeWalker) walker?.StartMoving();
        if (resumeWalkerV2) walkerV2?.StartWalking();
        if (resumeCrawler) crawler?.StartCrawling();
    }

    /// <summary>销毁随从时不恢复任何原生协程。</summary>
    public void Dispose() => Release(false);
}

