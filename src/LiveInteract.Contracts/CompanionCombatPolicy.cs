using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveInteract.Contracts;

/// <summary>友军点名与移动的纯判定，不执行攻击、不修改原作状态机。</summary>
public static class CompanionCombatPolicy
{
    // 出招、受击、死亡和变身必须完成后再换目标。
    private static readonly string[] committedStates = { "attack", "antic", "slash", "shoot", "firing", "spit", "strike", "dash", "charge", "recover", "recoil", "stun", "transform", "death", "die", "roar" };

    /// <summary>原作特殊死亡可能先归零再转换阶段，仅清理已真正结束的本体。</summary>
    /// <param name="missing">原生生命组件是否已经销毁。</param>
    /// <param name="dead">原作是否已判定死亡。</param>
    /// <param name="hp">当前原作生命。</param>
    /// <param name="specialDeath">是否由原作特殊死亡状态机处理归零。</param>
    /// <returns>是否应回收召唤根对象。</returns>
    public static bool ShouldRemoveNativeRoot(bool missing, bool dead, int hp, bool specialDeath)
        => missing || dead || (hp <= 0 && !specialDeath);

    /// <summary>只有主播来源且真正扣血的有效命中能够点名。</summary>
    /// <param name="before">命中前生命。</param>
    /// <param name="after">命中后生命。</param>
    /// <param name="playerSource">是否来自主播武器或技能。</param>
    /// <param name="friendlySource">来源是否为友军或其派生物。</param>
    /// <param name="friendlyFlow">是否在友军攻击调用内。</param>
    /// <returns>是否更新集火目标。</returns>
    public static bool IsPlayerDesignation(int before, int after, bool playerSource, bool friendlySource, bool friendlyFlow)
        => after < before && playerSource && !friendlySource && !friendlyFlow;

    /// <summary>出招期间保持目标，避免瞄准和动画中途翻转。</summary>
    /// <param name="states">原作当前状态及动画名称。</param>
    /// <param name="asset">单位标识，用于核对过的原作招式连续状态。</param>
    /// <returns>是否已到安全衔接点。</returns>
    public static bool CanChangeTarget(IEnumerable<string> states, string asset = "")
        => !states.Any(state => state != "Attack Fly" && (committedStates.Any(word => (state ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
            || IsVerifiedAttackContinuation(asset, state ?? "")));

    /// <summary>保留原作FSM身份后判断安全转火，避免把父级模式名误作整个连招。</summary>
    /// <param name="states">当前启用FSM的名称和状态。</param>
    /// <param name="animations">原作当前动画和原生友军状态。</param>
    /// <param name="asset">召唤目录标识。</param>
    /// <returns>是否处于已核对的安全衔接点。</returns>
    public static bool CanChangeTarget(IEnumerable<(string Fsm, string State)> states, IEnumerable<string> animations, string asset = "")
    {
        var current = states.ToArray();
        // 原作外层Attack模式持续到子FSM收尾；仅子FSM已回Idle才是已核对的安全边界。
        var nestedAttackIdle = (asset is "spawn.pilgrim-staffwielder" or "spawn.pilgrim-01" or "spawn.pilgrim-staff-understore" or "spawn.pilgrim-03")
            && current.Any(s => s.Fsm == "Attack" && s.State == "Idle");
        return CanChangeTarget(current.Where(s => !(nestedAttackIdle && s.Fsm == "pilgrim_behaviour"
                && s.State is "Attack" or "Attack Recover"))
            .Select(s => s.State).Concat(animations), asset);
    }

    /// <summary>原作招式自然结束后、下一状态动作执行前的短暂安全转火点。</summary>
    /// <param name="asset">已核对的召唤目录标识。</param>
    /// <param name="fsm">原生状态机名。</param>
    /// <param name="state">原生刚进入的状态，不请求或强设状态。</param>
    /// <returns>是否已完成原招且可接收主播点名。</returns>
    public static bool IsRetargetEntryBoundary(string asset, string fsm, string state)
        => fsm == "Control" && (asset, state) is
            ("spawn.flower-drifter", "Idle Start") or
            ("spawn.coral-swimmer-fat", "Check Return") or
            ("spawn.mitefly", "Attack End");

    // These generic state names are attack continuations only in these verified native graphs.
    private static bool IsVerifiedAttackContinuation(string asset, string state)
        => asset switch
        {
            "spawn.song-pilgrim-maestro" => state.StartsWith("Defend", StringComparison.OrdinalIgnoreCase)
                || state.StartsWith("Point", StringComparison.OrdinalIgnoreCase)
                || state.IndexOf("summon", StringComparison.OrdinalIgnoreCase) >= 0
                || state.Equals("Call", StringComparison.OrdinalIgnoreCase),
            "spawn.mossbone-mother" => state.StartsWith("Slam", StringComparison.OrdinalIgnoreCase)
                || state.StartsWith("Swoop", StringComparison.OrdinalIgnoreCase)
                || state.Equals("Fly Up", StringComparison.OrdinalIgnoreCase)
                || state.Equals("Anim End", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    /// <summary>识别已经从原作状态图核对的中性移动/休息状态。</summary>
    /// <param name="state">原作状态名。</param>
    /// <returns>是否可暂停移动计时而不截断招式。</returns>
    public static bool IsIdleLocomotion(string state) => state is "Patrol" or "Patrolling" or "Walk" or "Walk Slow" or "Walking" or "Idle" or "Pause" or "Start Walker"
        or "Ground Idle" or "Idle Fly" or "Unalert Fly" or "Idle Follow" or "Rest" or "Follow" or "Arrive" or "Aggro" or "Short Aggro" or "Fly" or "Walker"
        or "Run" or "Crawling" or "Chase Target" or "Idle Dart" or "Crawl" or "Crawl Idle" or "Submerged" or "Roost" or "Rest Wall" or "Attack Fly" or "Chase v2";

    /// <summary>仅暂挂可用的原生移动，不能阻断通往初始化的短暂等待。</summary>
    /// <param name="hasMoveClip">是否具有可用的原生移动配置及动画。</param>
    /// <param name="nextStates">当前状态的后继状态。</param>
    /// <returns>是否能够安全接管随行。</returns>
    public static bool CanSuspendIdle(bool hasMoveClip, IEnumerable<string> nextStates)
        => hasMoveClip && !nextStates.Any(state => (state ?? "").StartsWith("Init", StringComparison.OrdinalIgnoreCase) || state == "Respawn Setup");

    /// <summary>由当前中性移动动作决定走路或飞行，攻击时的短暂飞行不改变随行类型。</summary>
    /// <param name="currentGroundMove">当前状态是否在走路或地面爬行。</param>
    /// <param name="currentFlightMove">当前状态是否使用原生飞行移动。</param>
    /// <param name="groundProfile">原作是否有地面行走配置。</param>
    /// <param name="flightProfile">原作是否有飞行动作。</param>
    /// <returns>是否按飞行随行。</returns>
    public static bool UsesFlightLocomotion(bool currentGroundMove, bool currentFlightMove, bool groundProfile, bool flightProfile)
        => !currentGroundMove && (currentFlightMove || !groundProfile && flightProfile);

    /// <summary>仅在原作安全移动状态下随行或靠近尚未进入感知区的敌人。</summary>
    /// <param name="hasEnemy">是否已有有效敌人。</param>
    /// <param name="targetInNativeRange">敌人是否进入原作感知区。</param>
    /// <param name="nativeFriendly">是否为无需接管移动的原生小弟。</param>
    /// <param name="safeLocomotion">当前是否为可接管的原作移动状态。</param>
    /// <returns>是否允许随行或接敌。</returns>
    public static bool CanSteerIdle(bool hasEnemy, bool targetInNativeRange, bool nativeFriendly, bool safeLocomotion)
        => !nativeFriendly && safeLocomotion && (!hasEnemy || !targetInNativeRange);
    /// <summary>允许已核对的休眠状态通过自己的苏醒事件进入原作移动链。</summary>
    /// <param name="state">原作当前状态名。</param>
    /// <param name="hasWakeTransition">当前状态是否具有已核对的原作苏醒事件。</param>
    /// <param name="hasEnemy">是否发现有效敌人。</param>
    /// <param name="heroDistance">与主播的水平距离。</param>
    /// <param name="asset">特殊苏醒适配的召唤目录标识。</param>
    /// <param name="fsm">特殊苏醒适配的原作状态机身份。</param>
    /// <returns>是否发送原作苏醒事件。</returns>
    public static bool ShouldWakeForCompanion(string state, bool hasWakeTransition, bool hasEnemy, float heroDistance, string asset = "", string fsm = "")
        => hasWakeTransition && (state is "Ambush Wait" or "Battle Dormant" or "Drop Wait"
            || (hasEnemy || heroDistance > 3.5f)
                && (state is "Hiding" or "Sleep" or "Sleeping" or "Camp Idle" or "Pray" or "Eating" or "Ambush Ready" or "Rest"
                    || (asset is "spawn.gloom-beast" or "spawn.seth" or "spawn.conductor-boss") && fsm == "Control" && state == "Dormant"
                    || asset == "spawn.stick-insect-charger" && fsm == "Behaviour Base" && state == "Hidden"));
    /// <summary>最终之爪沿自身Guard出场链起飞，不跳入攻击态。</summary>
    /// <param name="asset">召唤目录标识。</param>
    /// <param name="fsm">原作状态机身份。</param>
    /// <param name="state">原作当前状态。</param>
    /// <param name="originalBattleWaitTransition">是否具有已核对的原作BATTLE START出场转移。</param>
    /// <returns>是否发送原作出场事件。</returns>
    public static bool ShouldStartCompanionEntrance(string asset, string fsm, string state, bool originalBattleWaitTransition)
        => originalBattleWaitTransition && fsm == "Control" && (asset == "spawn.bone-hunter-chief" && state == "Guard"
            || asset == "spawn.coral-conch-driller-giant" && state == "Dormant");

    /// <summary>Walker启动动作可位于前置状态，持续行走状态仍需接管该组件。</summary>
    /// <param name="state">原作当前状态名。</param>
    /// <param name="hasWalker">是否存在可用的原生Walker。</param>
    /// <param name="hasStartAction">当前状态是否有StartWalker。</param>
    /// <returns>是否使用Walker的原生行走参数。</returns>
    public static bool CanUseWalkerLocomotion(string state, bool hasWalker, bool hasStartAction)
        => hasWalker && IsIdleLocomotion(state)
            && (hasStartAction || state is "Walk" or "Walker" or "Walking" or "Patrol" or "Patrolling");
    /// <summary>地面Crawler启动动作结束后仍可接管，贴墙和攻击阶段继续使用原作逻辑。</summary>
    /// <param name="state">当前原作状态。</param>
    /// <param name="floor">是否为地面Crawler。</param>
    /// <param name="running">原生爬行是否仍在运行。</param>
    /// <param name="startAction">当前状态是否启动爬行。</param>
    /// <returns>是否可使用该原生地面爬行配置。</returns>
    public static bool CanUseCrawlerLocomotion(string state, bool floor, bool running, bool startAction)
        => floor && IsIdleLocomotion(state) && (running || startAction);

    /// <summary>有当前原生距离飞行动作的 Ready 才可接管，出生/战斗准备继续运行。</summary>
    public static bool IsNeutralFlightReady(string state, bool distanceFlight)
        => state == "Ready" && distanceFlight;

    /// <summary>已核对的顶挂飞虫通过自己的 ALERT→下落→飞行链离顶，不直接跳进攻击。</summary>
    public static bool ShouldLaunchCeilingFlyer(string state, bool alertTransition, bool flightProfile, bool hasEnemy, float heroDistance)
        => state == "In Ceiling" && alertTransition && flightProfile && (hasEnemy || heroDistance > 3.5f);
    /// <summary>仅对已核对的原作休眠入口发送 ALERT，不能通用唤醒 Boss。</summary>
    /// <param name="state">当前原作状态。</param>
    /// <param name="alertTransition">当前是否具有原作ALERT转换。</param>
    /// <param name="hasEnemy">是否存在有效敌人。</param>
    /// <param name="heroDistance">距离主播的水平距离。</param>
    /// <param name="asset">精确特殊适配的召唤标识，旧入口默认不启用特殊适配。</param>
    /// <param name="fsm">特殊适配的原作状态机身份。</param>
    /// <returns>是否允许原作苏醒事件，不直接切换状态。</returns>
    public static bool ShouldAlertForCompanion(string state, bool alertTransition, bool hasEnemy, float heroDistance, string asset = "", string fsm = "")
        => alertTransition && (hasEnemy || heroDistance > 3.5f)
            && (state is "Munching" or "Rest" or "Ambush Ready" or "Sleep"
                || asset == "spawn.rosary-pilgrim" && fsm == "Control" && state == "Grave Pray");
    /// <summary>只接管已核对且实际带飞行动作的空闲循环，保留出场与攻击链。</summary>
    public static bool IsNeutralNativeFlight(string fsm, string state, bool flightAction, string asset = "")
        => flightAction && (fsm == "Bouncer Control" && state == "Fly 2"
            || fsm == "Control" && state is "Ready" or "Flapping" or "Fly Idle" or "Woken Idle" or "Start Idle"
            || asset == "spawn.pilgrim-03-song" && fsm == "Control" && state == "Out of Range"
            || state is "To Patrol" or "To Start" && (fsm == "Control" && asset is
                "spawn.coral-swimmer-fat" or "spawn.pilgrim-fly" or "spawn.poke-swimmer" or "spawn.pilgrim-04-song"
                || fsm == "Attack" && asset == "spawn.pilgrim-stomper-song"));
    /// <summary>按已核对的攻击感应区交回原作战斗，不在较大的警戒区提前停住。</summary>
    /// <param name="asset">召唤目录标识。</param>
    /// <param name="fsm">当前原作状态机。</param>
    /// <param name="state">当前中性移动状态。</param>
    /// <returns>已核对的攻击区名称；其余单位沿用原有检查。</returns>
    public static string? PreferredCombatApproachRange(string asset, string fsm, string state)
        => asset == "spawn.citadel-bat" && fsm == "Control" && state == "Flapping" ? "Attack Range" : null;

    /// <summary>骷王召唤副本沿自身演奏唤醒入口出场，不跳到战斗态或屋顶分支。</summary>
    public static bool ShouldWakeSkullCompanion(string asset, string fsm, string state, bool wakeAnticTransition)
        => asset == "spawn.skull-king" && fsm == "Behaviour" && state == "State Check" && wakeAnticTransition;

    /// <summary>已核对的采集飞虫在停靠时可沿自身原生飞行配置随行，不发送带攻击的ALERT。</summary>
    public static bool IsNeutralPerchedFlight(string asset, string fsm, string state, bool nativeFlight)
        => nativeFlight && asset == "spawn.aspid-collector" && fsm == "Control" && state == "Wall";

    /// <summary>区分没有敌人和敌人在射程外；保留原招恢复事件，不从空闲误启动战斗传送。</summary>
    /// <param name="asset">召唤目录身份。</param>
    /// <param name="fsm">原作控制器。</param>
    /// <param name="state">原作当前状态。</param>
    /// <param name="range">原感知区名称。</param>
    /// <param name="eventName">原动作准备发出的事件。</param>
    /// <param name="hasEnemy">是否存在有效敌人。</param>
    /// <returns>是否保留此次原作感知事件。</returns>
    public static bool ShouldEmitNamedRangeEvent(string asset, string fsm, string state, string range, string eventName, bool hasEnemy)
        => hasEnemy || asset != "spawn.seth" || fsm != "Control" || state != "Idle" || range != "Throw Range" || eventName != "TELE OUT";

    /// <summary>限定可接管本体随行的原生控制器，避免辅助武器的空闲态抢占本体动作。</summary>
    /// <param name="asset">召唤目录标识。</param>
    /// <param name="fsm">原作状态机名。</param>
    /// <param name="isRoot">状态机是否直接挂在召唤根节点。</param>
    /// <returns>是否允许此控制器参与随行接管。</returns>
    public static bool CanUseLocomotionController(string asset, string fsm, bool isRoot)
        => asset != "spawn.seth" || isRoot && fsm == "Control";

    /// <summary>没有步行动作的已核对单位只在各自中性等待态低速悬浮随行。</summary>
    public static bool UsesStationaryHoverFollow(string asset, string fsm, string state)
        => fsm == "Control" && (asset == "spawn.rhino" && state is "Wait" or "Idle" or "Wall Idle"
            || asset == "spawn.seth" && state == "Idle");

    /// <summary>已核对的原作接触巡游将自己的移动目标绑定到敌人，不改出生或返场锚点。</summary>
    public static bool IsVerifiedPursuitVariable(string asset, string fsm, string variable)
        => asset == "spawn.spike-lazy-flyer" && fsm == "Control" && variable == "Next Point";

    /// <summary>拟草虫仅使用原作启用的身体接触伤害接敌，不另造攻击或扩大碰撞。</summary>
    public static bool UsesContinuousContactGround(string asset, string fsm, string state, bool activeFloorBodyWeapon)
        => activeFloorBodyWeapon && asset == "spawn.fields-goomba" && fsm == "Behaviour" && state is "Idle" or "Walk" or "Run Away";

    /// <summary>场景节点巡游的潘德拉戈只适配中性飞行，攻击保留原作身体武器。</summary>
    /// <param name="asset">召唤单位标识。</param>
    /// <param name="fsm">原作状态机名。</param>
    /// <param name="state">原作当前状态。</param>
    /// <param name="nativeTween">是否存在原作移动补间。</param>
    /// <param name="activeBodyWeapon">原作身体武器是否仍有效。</param>
    /// <returns>是否由物理随行层接管已核对的原场景巡航。</returns>
    public static bool UsesContinuousTweenFlight(string asset, string fsm, string state, bool nativeTween, bool activeBodyWeapon)
        => nativeTween && activeBodyWeapon && asset == "spawn.dragonfly-large" && fsm == "Control" && state == "Move Pos";

    /// <summary>幼蝇的原生游飞即接触攻击；只有其身体伤害启用时持续接近。</summary>
    public static bool UsesContinuousContactFlight(string fsm, string state, bool activeBodyWeapon)
        => activeBodyWeapon && fsm == "Bouncer Control" && state == "Fly 2";
    /// <summary>幼蝇只触发已核对的原生孵化链，不跳过孵化过程或进入攻击态。</summary>
    public static bool ShouldHatchForCompanion(string fsm, string state, bool hatchTransition, bool hasEnemy, float heroDistance)
    {
        // 孵化是隐藏出生流程；近处出生也必须显出本体，距离参数保留原有调用兼容。
        return fsm == "Bouncer Control" && state == "In Egg" && hatchTransition;
    }

    /// <summary>带原生距离飞行的追击循环在脱战后可以恢复随行。</summary>
    public static bool IsNeutralChaseFlight(string fsm, string state, bool distanceFlight, string asset = "")
    {
        // 圣咏长老原作主AI名为Attack；Chase只有DistanceFly/感知检查，实际践踏在后续招式态。
        return state == "Chase" && distanceFlight && (fsm == "Control"
            || asset == "spawn.pilgrim-stomper-song" && fsm == "Attack");
    }

    /// <summary>友军攻击触发器不能把主播或自己人视为命中目标；敌方原作事件保持不变。</summary>
    /// <param name="sourceFriendly">触发器是否属于友方召唤本体或其派生物。</param>
    /// <param name="targetHero">实际接触的碰撞体是否属于主播。</param>
    /// <param name="targetFriendly">实际接触的碰撞体是否属于友军。</param>
    /// <returns>是否阻止这次阵营不合法的原作触发事件。</returns>
    public static bool ShouldIgnoreFriendlyContactEvent(bool sourceFriendly, bool targetHero, bool targetFriendly)
        => sourceFriendly && (targetHero || targetFriendly);

    /// <summary>无敌人随行时暂停已核对的跌落传送检测，不在屏内用传送代替走路。</summary>
    /// <param name="fsm">原作状态机名。</param>
    /// <param name="state">原作当前状态。</param>
    /// <param name="hasEnemy">是否正在战斗。</param>
    /// <returns>是否暂停该辅助检测的更新。</returns>
    public static bool ShouldPauseFollowFallTeleport(string fsm, string state, bool hasEnemy)
        => !hasEnemy && fsm == "Fall Tele" && state == "Check";
    /// <summary>隐身出生的焚身信徒只在有敌人时执行原作警觉传送；随行不触发该分支。</summary>
    /// <param name="asset">召唤目录标识。</param>
    /// <param name="fsm">原作状态机名。</param>
    /// <param name="variable">原作局部开关名。</param>
    /// <param name="hasEnemy">当前是否有有效敌人。</param>
    /// <returns>是否清除仅用于敌情的出生警觉开关。</returns>
    public static bool ShouldClearCompanionStartle(string asset, string fsm, string variable, bool hasEnemy)
        => !hasEnemy && asset == "spawn.farmer-wisp" && fsm == "Control" && variable == "Do Tele Startle";
    /// <summary>原作直接伤害也必须来自该友军的有效攻击碰撞，并实际接触当前敌人身体。</summary>
    /// <param name="damage">原作本次直接伤害值。</param>
    /// <param name="sourceOwned">实际伤害物是否属于该友军或其派生物。</param>
    /// <param name="activeCollider">实际伤害物是否存在启用且激活的碰撞体。</param>
    /// <param name="targetBodyContact">碰撞是否接触有效敌人身体而非警戒触发框。</param>
    /// <returns>是否允许结算本次直接伤害。</returns>
    public static bool CanRouteDirectDamage(int damage, bool sourceOwned, bool activeCollider, bool targetBodyContact)
        => damage > 0 && sourceOwned && activeCollider && targetBodyContact;
    /// <summary>召唤本体、友军派生物和延迟特效使用同一阵营判定。</summary>
    /// <param name="companion">是否属于友军召唤本体层级。</param>
    /// <param name="projectile">是否带有仍为友军的原作派生物标记。</param>
    /// <param name="resource">是否属于已登记的友军临时资源。</param>
    /// <returns>是否必须按友方实体处理。</returns>
    public static bool IsFriendlyEntity(bool companion, bool projectile, bool resource)
        => companion || projectile || resource;
}
