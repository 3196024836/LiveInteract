using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>验证点名来源、攻击安全衔接及距离计算的战斗契约。</summary>
public sealed class CompanionCombatPolicyTests
{
    [Theory]
    [InlineData("spawn.citadel-bat", "Control", "Flapping", "Attack Range")]
    [InlineData("spawn.citadel-bat", "Control", "Roost", null)]
    [InlineData("spawn.citadel-bat", "Control", "Charge Antic", null)]
    [InlineData("spawn.citadel-bat", "Control", "Charge", null)]
    [InlineData("spawn.citadel-bat", "Flap", "Flap Up", null)]
    [InlineData("spawn.other", "Control", "Flapping", null)]
    [InlineData("ally.clockwork", "Control", "Flapping", null)]
    public void CitadelBatApproachesActualAttackRangeWithoutReplacingItsCharge(string asset, string fsm, string state, string? expected)
        => Assert.Equal(expected, CompanionCombatPolicy.PreferredCombatApproachRange(asset, fsm, state));

    [Theory]
    [InlineData("spawn.dragonfly-large", "Control", "Move Pos", true, true, true)]
    [InlineData("spawn.dragonfly-large", "Control", "Antic", true, true, false)]
    [InlineData("spawn.dragonfly-large", "Control", "Recoil Wait", true, true, false)]
    [InlineData("spawn.dragonfly-large", "Control", "Stop", true, true, false)]
    [InlineData("spawn.dragonfly-large", "Auxiliary", "Move Pos", true, true, false)]
    [InlineData("spawn.dragonfly-large", "Control", "Move Pos", false, true, false)]
    [InlineData("spawn.dragonfly-large", "Control", "Move Pos", true, false, false)]
    [InlineData("spawn.other", "Control", "Move Pos", true, true, false)]
    [InlineData("ally.clockwork", "Control", "Move Pos", true, true, false)]
    public void VerifiedDragonflyPatrolFlightKeepsOnlyItsOriginalBodyWeapon(string asset, string fsm, string state, bool tween, bool bodyWeapon, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesContinuousTweenFlight(asset, fsm, state, tween, bodyWeapon));

    [Theory]
    [InlineData(10, 9, true, false, false, true)]
    [InlineData(10, 10, true, false, false, false)]
    [InlineData(10, 9, false, false, false, false)]
    [InlineData(10, 9, true, true, false, false)]
    [InlineData(10, 9, true, false, true, false)]
    public void OnlyActualPlayerDamageDesignates(int before, int after, bool player, bool friendly, bool flow, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsPlayerDesignation(before, after, player, friendly, flow));

    [Theory]
    [InlineData("spawn.flower-drifter", "Control", "Idle Start", true)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "Check Return", true)]
    [InlineData("spawn.mitefly", "Control", "Attack End", true)]
    [InlineData("spawn.bone-circler-vicious", "Control", "Attack End", true)]
    [InlineData("spawn.bone-circler-vicious", "Auxiliary", "Attack End", false)]
    [InlineData("spawn.flower-drifter", "Control", "Attack Recover", false)]
    [InlineData("spawn.flower-drifter", "Control", "Attack Burst", false)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "Attack Antic", false)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "Attack", false)]
    [InlineData("spawn.coral-swimmer-fat", "Auxiliary", "Check Return", false)]
    [InlineData("spawn.other", "Control", "Check Return", false)]
    [InlineData("ally.clockwork", "Control", "Idle Start", false)]
    public void NativeCompletionBoundaryIsQualifiedAndDoesNotInterruptAttack(string asset, string fsm, string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsRetargetEntryBoundary(asset, fsm, state));

    [Theory]
    [InlineData("Attack Fly", true)][InlineData("Chase v2", true)]
    [InlineData("Idle", true)][InlineData("Start Walker", true)][InlineData("Chase", true)]
    [InlineData("Shoot Antic", false)][InlineData("Slash Recover", false)][InlineData("Firing Loop", false)]
    [InlineData("Transform", false)][InlineData("Death", false)][InlineData("Attack 2", false)]
    public void FocusWaitsForSafeAttackBoundary(string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanChangeTarget(new[] { state }));

    [Theory]
    [InlineData("spawn.song-pilgrim-maestro", "Defend Start", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Defending", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Defend End", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Point Start", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Call", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Summon", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Summon?", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Entry Summon?", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Resummon?", false)]
    [InlineData("spawn.song-pilgrim-maestro", "Start Idle", true)]
    [InlineData("spawn.song-pilgrim-maestro", "Fly", true)]
    [InlineData("spawn.mossbone-mother", "Slam", false)]
    [InlineData("spawn.mossbone-mother", "Slam Wait", false)]
    [InlineData("spawn.mossbone-mother", "Fly Up", false)]
    [InlineData("spawn.mossbone-mother", "Anim End", false)]
    [InlineData("spawn.mossbone-mother", "Swoop", false)]
    [InlineData("spawn.mossbone-mother", "Woken Idle", true)]
    [InlineData("spawn.mossbone-mother", "Idle", true)]
    [InlineData("spawn.other", "Fly Up", true)]
    [InlineData("spawn.other", "Call", true)]
    public void VerifiedBossAttackContinuationsKeepTheirTarget(string asset, string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanChangeTarget(new[] { state }, asset));

    [Theory]
    [InlineData("spawn.pilgrim-01", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Walk", true)]
    [InlineData("spawn.pilgrim-01", "pilgrim_behaviour", "Attack", "Attack", "Idle", "Turn", true)]
    [InlineData("spawn.pilgrim-01", "pilgrim_behaviour", "Attack", "Attack", "Attack Air", "AttackAir", false)]
    [InlineData("spawn.pilgrim-01", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "AttackLand", false)]
    [InlineData("spawn.pilgrim-01", "Other Controller", "Attack Recover", "Attack", "Idle", "Walk", false)]
    [InlineData("spawn.pilgrim-03", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Walk", true)]
    [InlineData("spawn.pilgrim-03", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Turn", true)]
    [InlineData("spawn.pilgrim-03", "pilgrim_behaviour", "Attack", "Attack", "Attack Antic", "Attack Antic", false)]
    [InlineData("spawn.pilgrim-03", "pilgrim_behaviour", "Attack", "Attack", "Wait For Land", "Attack", false)]
    [InlineData("spawn.pilgrim-03", "Other", "Attack Recover", "Attack", "Idle", "Walk", false)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Walk", true)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Turn", true)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Idle", "Idle", true)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Stab Antic", "Stab Antic", false)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Stab", "Stab", false)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Stab End", "Stab End", false)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Air", "Leap", false)]
    [InlineData("spawn.pilgrim-staff-understore", "pilgrim_behaviour", "Attack", "Attack", "Attack Recover", "Jump Attack", false)]
    [InlineData("spawn.pilgrim-staff-understore", "Other", "Attack Recover", "Attack", "Idle", "Walk", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Walk", true)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Idle", "Idle", true)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Idle", "Attack Antic", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Jump", "Jump", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Air", "Air", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Turn?", "Idle", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Recover Idle", "Idle", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack", "Attack", "Attack 3", "Attack", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Stun", false)]
    [InlineData("spawn.pilgrim-staffwielder", "Other Controller", "Attack Recover", "Attack", "Idle", "Walk", false)]
    [InlineData("spawn.pilgrim-staffwielder", "pilgrim_behaviour", "Attack Recover", "Other", "Idle", "Walk", false)]
    [InlineData("spawn.pilgrim-02-song", "pilgrim_behaviour", "Attack Recover", "Attack", "Idle", "Walk", true)]
    [InlineData("spawn.pilgrim-03-understore", "pilgrim_behaviour", "Attack", "Attack", "Skid", "Attack Land", false)]
    public void VerifiedPilgrimNestedAttackControllerUsesOnlyItsIdleBoundary(string asset, string parentFsm, string parentState,
        string childFsm, string childState, string animation, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanChangeTarget(
            new[] { (parentFsm, parentState), (childFsm, childState) }, new[] { animation }, asset));

    [Theory]
    [InlineData("Attack Fly", true)][InlineData("Chase v2", true)]
    [InlineData("Ground Idle", true)][InlineData("Idle Fly", true)][InlineData("Unalert Fly", true)]
    [InlineData("Idle Follow", true)][InlineData("Rest", true)][InlineData("Follow", true)]
    [InlineData("Arrive", true)][InlineData("Aggro", true)][InlineData("Throw Antic", false)]
    [InlineData("Slash", false)][InlineData("Init", false)][InlineData("Stun", false)]
    public void RecognizesNativeNeutralLocomotionWithoutTakingAttacks(string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsIdleLocomotion(state));

    [Theory]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, true, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, true, false, false)]
    public void AttackOnlyFlightDoesNotMakeGroundFollowersHover(bool ground, bool flight, bool groundProfile, bool flightProfile, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesFlightLocomotion(ground, flight, groundProfile, flightProfile));

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public void ApproachOnlyWhileNativeLocomotionIsSafe(bool enemy, bool nativeRange, bool nativeFriendly, bool safe, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanSteerIdle(enemy, nativeRange, nativeFriendly, safe));
    [Theory]
    [InlineData(false, "Idle", false)]
    [InlineData(true, "Init", false)]
    [InlineData(true, "Init 2", false)]
    [InlineData(true, "Respawn Setup", false)]
    [InlineData(true, "Walk", true)]
    [InlineData(true, "Attack", true)]
    [InlineData(true, "Idle", true)]
    public void InitializationWaitsAndMissingMotionNeverSuspendTheNativeFsm(bool hasMoveClip, string destination, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanSuspendIdle(hasMoveClip, new[] { destination }));
    [Theory]
    [InlineData("Sleep", true, false, 8, true)]
    [InlineData("Sleeping", true, false, 8, true)]
    [InlineData("Camp Idle", true, false, 8, true)]
    [InlineData("Pray", true, false, 8, true)]
    [InlineData("Hiding", true, false, 8, true)]
    [InlineData("Sleep", true, true, 1, true)]
    [InlineData("Sleep", true, false, 2.5, false)]
    [InlineData("Sleep", false, true, 8, false)]
    [InlineData("Dormant", true, true, 8, false)]
    [InlineData("Shop Startle", true, true, 8, false)]
    [InlineData("Attack", true, true, 8, false)]
    [InlineData("Init", true, true, 8, false)]
    public void OnlyVerifiedSleepStatesUseTheirOwnWakeEvent(string state, bool wake, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, wake, enemy, distance));

    [Fact]
    public void NativeWalkerNeutralStateCanFollowWithoutTakingAnAttack()
        => Assert.True(CompanionCombatPolicy.IsIdleLocomotion("Walker"));
    [Theory]
    [InlineData("Walk", true, false, true)]
    [InlineData("Walker", true, false, true)]
    [InlineData("Walking", true, false, true)]
    [InlineData("Patrol", true, false, true)]
    [InlineData("Idle", true, true, true)]
    [InlineData("Walk", false, false, false)]
    [InlineData("Idle", true, false, false)]
    [InlineData("Attack", true, true, false)]
    [InlineData("Init", true, true, false)]
    public void WalkerCanKeepRunningAfterItsStartActionFinished(string state, bool walker, bool startAction, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanUseWalkerLocomotion(state, walker, startAction));
    [Theory]
    [InlineData("Crawl", true, true, false, true)]
    [InlineData("Crawl Idle", true, true, false, true)]
    [InlineData("Idle", true, false, true, true)]
    [InlineData("Crawl", false, true, false, false)]
    [InlineData("Crawl", true, false, false, false)]
    [InlineData("Attack", true, true, true, false)]
    [InlineData("Init", true, true, true, false)]
    public void CrawlerKeepsItsNativeFloorProfileAfterTheStartActionFinished(string state, bool floor, bool running, bool startAction, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanUseCrawlerLocomotion(state, floor, running, startAction));
    [Theory]
    [InlineData("Chase Target", true)][InlineData("Idle Dart", true)]
    [InlineData("Patrol Pause", false)][InlineData("Jump Attack", false)]
    public void VerifiedFreeFlightStatesDoNotSuspendAttackAnimations(string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsIdleLocomotion(state));

    [Fact]
    public void AttachedMiteUsesItsOwnDetachWakeChainWhenFollowing()
        => Assert.True(CompanionCombatPolicy.ShouldWakeForCompanion("Eating", true, false, 8));

    [Fact]
    public void GroundMiteRunIsNativeWalkingNotAnAttack()
        => Assert.True(CompanionCombatPolicy.IsIdleLocomotion("Run"));
    [Theory]
    [InlineData(true, false, 8, true)]
    [InlineData(true, true, 1, true)]
    [InlineData(true, false, 2.5, false)]
    [InlineData(false, true, 8, false)]
    public void HiddenHunterUsesOriginalAmbushWake(bool wake, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion("Ambush Ready", wake, enemy, distance));
    [Theory]
    [InlineData("Ready", true, true)]
    [InlineData("Ready", false, false)]
    [InlineData("Attack", true, false)]
    [InlineData("Fly In Ready", true, false)]
    public void OnlyAnActiveDistanceFlightMakesReadyNeutral(string state, bool distanceFlight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralFlightReady(state, distanceFlight));
    [Theory]
    [InlineData("In Ceiling", true, true, false, 8, true)]
    [InlineData("In Ceiling", true, true, true, 1, true)]
    [InlineData("In Ceiling", true, true, false, 2.5, false)]
    [InlineData("In Ceiling", false, true, true, 8, false)]
    [InlineData("In Ceiling", true, false, true, 8, false)]
    [InlineData("Dormant", true, true, true, 8, false)]
    public void CeilingFlyerUsesItsOriginalDropEntryOnly(string state, bool alert, bool flight, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldLaunchCeilingFlyer(state, alert, flight, enemy, distance));
    [Theory]
    [InlineData("Rest", true, false, 8, true)]
    [InlineData("Rest", true, true, 1, true)]
    [InlineData("Rest", false, true, 8, false)]
    [InlineData("Rest", true, false, 2.5, false)]
    public void RestingThrowerKeepsItsOriginalWakeChain(string state, bool wake, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, wake, enemy, distance));
    [Theory]
    [InlineData("Munching", true, false, 8, true)]
    [InlineData("Rest", true, true, 1, true)]
    [InlineData("Ambush Ready", true, false, 8, true)]
    [InlineData("Sleep", true, false, 8, true)]
    [InlineData("Rest", false, true, 8, false)]
    [InlineData("Rest", true, false, 2.5, false)]
    [InlineData("Dormant", true, true, 8, false)]
    [InlineData("Attack", true, true, 8, false)]
    public void VerifiedAlertWakeDoesNotStartArbitraryBossOrAttackStates(string state, bool alert, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldAlertForCompanion(state, alert, enemy, distance));
    [Theory]
    [InlineData("spawn.rosary-pilgrim", "Control", "Grave Pray", true, false, 8, true)]
    [InlineData("spawn.rosary-pilgrim", "Control", "Grave Pray", true, true, 1, true)]
    [InlineData("spawn.rosary-pilgrim", "Control", "Grave Pray", true, false, 2.5, false)]
    [InlineData("spawn.rosary-pilgrim", "Control", "Grave Pray", false, true, 8, false)]
    [InlineData("spawn.rosary-pilgrim", "Auxiliary", "Grave Pray", true, false, 8, false)]
    [InlineData("spawn.rosary-pilgrim", "Control", "Grave Pray End", true, false, 8, false)]
    [InlineData("spawn.rosary-pilgrim", "Control", "Whip 3", true, false, 8, false)]
    [InlineData("spawn.other", "Control", "Grave Pray", true, false, 8, false)]
    [InlineData("ally.clockwork", "Control", "Grave Pray", true, false, 8, false)]
    public void RosaryPrayerEndsThroughOriginalAlertEntryOnly(string asset, string fsm, string state, bool alert, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldAlertForCompanion(state, alert, enemy, distance, asset, fsm));

    [Fact]
    public void NativePersistentCrawlingIsNeutralButCrawlerAttackIsNot()
    {
        Assert.True(CompanionCombatPolicy.IsIdleLocomotion("Crawling"));
        Assert.False(CompanionCombatPolicy.IsIdleLocomotion("Attack Cooldown 1"));
    }
    [Theory]
    [InlineData("Control", "Start Idle", true, true)]
    [InlineData("Control", "Start Idle", false, false)]
    [InlineData("Other FSM", "Start Idle", true, false)]
    [InlineData("Control", "Defending", true, false)]
    [InlineData("Control", "Woken Idle", true, true)]
    [InlineData("Control", "Woken Idle", false, false)]
    [InlineData("Stun Control", "Woken Idle", true, false)]
    [InlineData("Control", "Flapping", true, true)]
    [InlineData("Control", "Fly Idle", true, true)]
    [InlineData("Bouncer Control", "Fly 2", true, true)]
    [InlineData("Control", "Ready", true, true)]
    [InlineData("Control", "Flapping", false, false)]
    [InlineData("Other FSM", "Fly 2", true, false)]
    [InlineData("Bouncer Control", "Attack", true, false)]
    [InlineData("Control", "Dormant", true, false)]
    public void VerifiedNativeFlightLoopsCanFollowWithoutTakingAttackOrBirthStates(string fsm, string state, bool flight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralNativeFlight(fsm, state, flight));
    [Theory]
    [InlineData("spawn.coral-swimmer-fat", "Control", "To Patrol", true, true)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "To Start", true, true)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "To Patrol", false, false)]
    [InlineData("spawn.coral-swimmer-fat", "Motion", "To Patrol", true, false)]
    [InlineData("spawn.coral-swimmer-fat", "Control", "Attack", true, false)]
    [InlineData("spawn.pilgrim-03-song", "Control", "Out of Range", true, true)]
    [InlineData("spawn.pilgrim-03-song", "Control", "Out of Range", false, false)]
    [InlineData("spawn.pilgrim-03-song", "Auxiliary", "Out of Range", true, false)]
    [InlineData("spawn.other", "Control", "Out of Range", true, false)]
    [InlineData("spawn.pilgrim-03-song", "Control", "Throw Antic", true, false)]
    public void BellguardDistanceFlightOutsideRangeIsNeutralOnlyInItsVerifiedController(string asset, string fsm, string state, bool flight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralNativeFlight(fsm, state, flight, asset));

    /// <summary>已实录的巡游飞行只接管对应本体控制器，不接管同名攻击或辅助状态。</summary>
    [Theory]
    [InlineData("spawn.pilgrim-fly", "Control", "To Patrol", true, true)]
    [InlineData("spawn.pilgrim-fly", "Control", "To Start", true, true)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "To Patrol", true, true)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "To Start", true, true)]
    [InlineData("spawn.poke-swimmer", "Control", "To Start", true, true)]
    [InlineData("spawn.pilgrim-04-song", "Control", "To Patrol", true, true)]
    [InlineData("spawn.pilgrim-04-song", "Control", "To Start", true, true)]
    [InlineData("spawn.pilgrim-fly", "Control", "To Patrol", false, false)]
    [InlineData("spawn.pilgrim-stomper-song", "Control", "To Patrol", true, false)]
    [InlineData("spawn.poke-swimmer", "Motion", "To Start", true, false)]
    [InlineData("spawn.pilgrim-04-song", "Auxiliary", "To Start", true, false)]
    [InlineData("spawn.other", "Control", "To Start", true, false)]
    [InlineData("ally.clockwork", "Control", "To Start", true, false)]
    [InlineData("spawn.pilgrim-fly", "Control", "Attack", true, false)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "Stomp Antic", true, false)]
    [InlineData("spawn.poke-swimmer", "Control", "Recover", true, false)]
    [InlineData("spawn.pilgrim-04-song", "Control", "Antic", true, false)]
    public void VerifiedNativePatrolFlightFollowsWithoutSuspendingOriginalAttack(string asset, string fsm, string state, bool flight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralNativeFlight(fsm, state, flight, asset));

    [Theory]
    [InlineData("Bouncer Control", "Fly 2", true, true)]
    [InlineData("Bouncer Control", "Fly 2", false, false)]
    [InlineData("Bouncer Control", "Wake", true, false)]
    [InlineData("Control", "Fly 2", true, false)]
    [InlineData("Control", "Fly", true, false)]
    public void ContactBouncersMustReachTheirRealBodyWeaponBeforeStopping(string fsm, string state, bool activeWeapon, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesContinuousContactFlight(fsm, state, activeWeapon));
    [Theory]
    [InlineData("Patrolling", true, true, true)]
    [InlineData("Patrolling", true, false, true)]
    [InlineData("Patrolling", false, true, false)]
    [InlineData("Attack", true, true, false)]
    public void WalkerV2PatrolKeepsItsNativeLocomotionProfile(string state, bool walker, bool startAction, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.CanUseWalkerLocomotion(state, walker, startAction));
    }

    [Theory]
    [InlineData("Bouncer Control", "In Egg", true, true, 1, true)]
    [InlineData("Bouncer Control", "In Egg", true, false, 8, true)]
    [InlineData("Bouncer Control", "In Egg", true, false, 2, true)]
    [InlineData("Bouncer Control", "In Egg", false, true, 8, false)]
    [InlineData("Control", "In Egg", true, true, 8, false)]
    [InlineData("Bouncer Control", "Dormant", true, true, 8, false)]
    [InlineData("Bouncer Control", "Hatch Antic", true, true, 8, false)]
    public void OnlyVerifiedEggEntryCanStartItsNativeHatch(string fsm, string state, bool hatch, bool enemy, float distance, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.ShouldHatchForCompanion(fsm, state, hatch, enemy, distance));
    }

    [Theory]
    [InlineData("Control", "Chase", true, true)]
    [InlineData("Control", "Chase", false, false)]
    [InlineData("Other FSM", "Chase", true, false)]
    [InlineData("Control", "Attack", true, false)]
    [InlineData("Control", "Dormant", true, false)]
    public void VerifiedDistanceChaseCanFollowWithoutTakingAttacks(string fsm, string state, bool distanceFlight, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.IsNeutralChaseFlight(fsm, state, distanceFlight));
    }

    [Theory]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "Chase", true, true)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "Chase", false, false)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "Stomp Antic", true, false)]
    [InlineData("spawn.pilgrim-stomper-song", "Attack", "Stomp", true, false)]
    [InlineData("spawn.other", "Attack", "Chase", true, false)]
    [InlineData("ally.clockwork", "Attack", "Chase", true, false)]
    public void OnlyVerifiedElderDistanceChaseCanReturnToFollow(string asset, string fsm, string state, bool distanceFlight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralChaseFlight(fsm, state, distanceFlight, asset));

    [Theory]
    [InlineData("spawn.skull-king", "Behaviour", "State Check", true, true)]
    [InlineData("spawn.skull-king", "Behaviour", "State Check", false, false)]
    [InlineData("spawn.skull-king", "Control", "State Check", true, false)]
    [InlineData("spawn.skull-king", "Behaviour", "In Roof", true, false)]
    [InlineData("spawn.skull-king", "Behaviour", "Wake Antic", true, false)]
    [InlineData("spawn.skull-king", "Behaviour", "Wake Roar", true, false)]
    [InlineData("spawn.other", "Behaviour", "State Check", true, false)]
    [InlineData("ally.clockwork", "Behaviour", "State Check", true, false)]
    public void SkullCompanionStartsOnlyItsVerifiedNaturalWakeChain(string asset, string fsm, string state, bool wakeAntic, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeSkullCompanion(asset, fsm, state, wakeAntic));

    [Theory]
    [InlineData("spawn.fields-goomba", "Behaviour", "Idle", true, true)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Walk", true, true)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Run Away", true, true)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Idle", false, false)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Run Away", false, false)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Call Antic", true, false)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Call", true, false)]
    [InlineData("spawn.fields-goomba", "Behaviour", "Death", true, false)]
    [InlineData("spawn.fields-goomba", "Control", "Idle", true, false)]
    [InlineData("spawn.other", "Behaviour", "Run Away", true, false)]
    [InlineData("ally.clockwork", "Behaviour", "Idle", true, false)]
    public void VerifiedGrassCrawlerApproachesOnlyWithItsOriginalFloorBodyWeapon(string asset, string fsm, string state, bool floorBodyWeapon, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesContinuousContactGround(asset, fsm, state, floorBodyWeapon));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void FriendlyContactEventsCannotConnectOnPlayerOrAllies(bool friendly, bool hero, bool ally, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldIgnoreFriendlyContactEvent(friendly, hero, ally));
    [Theory]
    [InlineData("Ambush Wait", true, false, 8, true)]
    [InlineData("Ambush Wait", true, true, 1, true)]
    [InlineData("Ambush Wait", true, false, 2.5f, true)]
    [InlineData("Battle Dormant", true, false, 2.5f, true)]
    [InlineData("Battle Dormant", true, false, 8, true)]
    [InlineData("Battle Dormant", false, true, 8, false)]
    [InlineData("Drop Wait", true, false, 2.5f, true)]
    [InlineData("Drop Wait", true, true, 8, true)]
    [InlineData("Drop Wait", false, true, 8, false)]
    [InlineData("Dormant", true, false, 8, false)]
    [InlineData("Ambush Wait", false, true, 8, false)]
    [InlineData("Tele In Antic", true, true, 8, false)]
    [InlineData("Cast Start", true, true, 8, false)]
    public void AmbushWaitUsesOriginalWakeInsteadOfStayingInvisible(string state, bool wake, bool enemy, float distance, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, wake, enemy, distance));
    }

    [Theory]
    [InlineData("Fall Tele", "Check", false, true)]
    [InlineData("Fall Tele", "Check", true, false)]
    [InlineData("Fall Tele", "Send Event", false, false)]
    [InlineData("Fall Tele", "Stop", false, false)]
    [InlineData("Control", "Check", false, false)]
    [InlineData("Control", "Tele Out 1", false, false)]
    public void FollowingCannotUseOriginalFallTeleportAsAWalkingShortcut(string fsm, string state, bool enemy, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.ShouldPauseFollowFallTeleport(fsm, state, enemy));
    }
    [Theory]
    [InlineData("spawn.farmer-wisp", "Control", "Do Tele Startle", false, true)]
    [InlineData("spawn.farmer-wisp", "Control", "Do Tele Startle", true, false)]
    [InlineData("spawn.farmer-wisp", "Control", "Did Tele", false, false)]
    [InlineData("spawn.farmer-wisp", "Burnup", "Do Tele Startle", false, false)]
    [InlineData("spawn.farmer-catcher", "Control", "Do Tele Startle", false, false)]
    public void WispNativeStartleNeedsAnEnemyBeforeTeleporting(string asset, string fsm, string variable, bool enemy, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.ShouldClearCompanionStartle(asset, fsm, variable, enemy));
    }
    [Theory]
    [InlineData(1, true, true, true, true)]
    [InlineData(0, true, true, true, false)]
    [InlineData(-1, true, true, true, false)]
    [InlineData(1, false, true, true, false)]
    [InlineData(1, true, false, true, false)]
    [InlineData(1, true, true, false, false)]
    [InlineData(1, false, false, false, false)]
    public void DirectDamageCannotBecomeRemoteOrAlertBoxDamage(int damage, bool owned, bool active, bool bodyContact, bool expected)
    {
        Assert.Equal(expected, CompanionCombatPolicy.CanRouteDirectDamage(damage, owned, active, bodyContact));
    }
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public void DetachedFriendlyChildrenRemainFriendlyTargetsAndDamageSources(bool companion, bool projectile, bool resource, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsFriendlyEntity(companion, projectile, resource));
    [Fact]
    public void NativeSlowWalkCanFollowAndStopWithoutChangingAttackStates()
    {
        Assert.True(CompanionCombatPolicy.IsIdleLocomotion("Walk Slow"));
        Assert.False(CompanionCombatPolicy.IsIdleLocomotion("Walk Attack"));
        Assert.False(CompanionCombatPolicy.IsIdleLocomotion("Walk Antic"));
    }
    [Theory]
    [InlineData("spawn.aspid-collector", "Control", "Wall", true, true)]
    [InlineData("spawn.aspid-collector", "Control", "Wall", false, false)]
    [InlineData("spawn.aspid-collector", "Other", "Wall", true, false)]
    [InlineData("spawn.aspid-collector", "Control", "Wall Startle", true, false)]
    [InlineData("spawn.aspid-collector", "Control", "Chomp", true, false)]
    [InlineData("spawn.shellwood-wasp", "Control", "Wall", true, false)]
    [InlineData("ally.clockwork", "Control", "Wall", true, false)]
    public void PerchedCollectorCanFollowWithoutFiringItsNativeStartleAttack(string asset, string fsm, string state, bool nativeFlight, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsNeutralPerchedFlight(asset, fsm, state, nativeFlight));
    [Theory]
    [InlineData("spawn.rhino", "Control", "Wait", true)]
    [InlineData("spawn.rhino", "Control", "Idle", true)]
    [InlineData("spawn.rhino", "Control", "Wall Idle", true)]
    [InlineData("spawn.rhino", "Other", "Wall Idle", false)]
    [InlineData("spawn.rhino", "Control", "Wall Impact", false)]
    [InlineData("spawn.rhino", "Control", "Charge Down", false)]
    [InlineData("spawn.rhino", "Other", "Wait", false)]
    [InlineData("spawn.rhino", "Control", "Idle Grunt", false)]
    [InlineData("spawn.rhino", "Control", "Charge Antic", false)]
    [InlineData("spawn.rhino", "Control", "Jump Attack", false)]
    [InlineData("spawn.rhino", "Control", "Death Air", false)]
    [InlineData("spawn.other", "Control", "Wait", false)]
    [InlineData("ally.clockwork", "Control", "Idle", false)]
    public void StationaryHoverUsesOnlyVerifiedNeutralRhinoStates(string asset, string fsm, string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesStationaryHoverFollow(asset, fsm, state));

    [Theory]
    [InlineData("spawn.spike-lazy-flyer", "Control", "Next Point", true)]
    [InlineData("spawn.spike-lazy-flyer", "Control", "Patrol Point", false)]
    [InlineData("spawn.spike-lazy-flyer", "Control", "Start Point", false)]
    [InlineData("spawn.spike-lazy-flyer", "Control", "Fly In Point", false)]
    [InlineData("spawn.spike-lazy-flyer", "Blocked Hit Recoil", "Next Point", false)]
    [InlineData("spawn.other", "Control", "Next Point", false)]
    [InlineData("ally.clockwork", "Control", "Next Point", false)]
    public void ContactPatrolRebindsOnlyItsVerifiedActualMovingTarget(string asset, string fsm, string variable, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.IsVerifiedPursuitVariable(asset, fsm, variable));

    [Theory]
    [InlineData("spawn.bone-hunter-chief", "Control", "Guard", true, true)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Guard", false, false)]
    [InlineData("spawn.bone-hunter-chief", "Other", "Guard", true, false)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Attack Choice", true, false)]
    [InlineData("spawn.bone-hunter-chief", "Control", "Fly Up", true, false)]
    [InlineData("spawn.zap-core-enemy", "Control", "Guard", true, false)]
    [InlineData("ally.clockwork", "Control", "Guard", true, false)]
    public void ChiefUsesOriginalBattleWaitEntranceInsteadOfChangingAttackState(string asset, string fsm, string state, bool transition, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldStartCompanionEntrance(asset, fsm, state, transition));

    [Theory]
    [InlineData("spawn.gloom-beast", "Control", "Dormant", true, false, 8, true)]
    [InlineData("spawn.gloom-beast", "Control", "Dormant", true, true, 1, true)]
    [InlineData("spawn.gloom-beast", "Control", "Dormant", true, false, 1, false)]
    [InlineData("spawn.gloom-beast", "Control", "Dormant", false, true, 8, false)]
    [InlineData("spawn.gloom-beast", "Other", "Dormant", true, true, 8, false)]
    [InlineData("spawn.gloom-beast", "Control", "Charge", true, true, 8, false)]
    [InlineData("spawn.swamp-shaman", "Control", "Dormant", true, true, 8, false)]
    [InlineData("spawn.coral-king", "Control", "Dormant", true, true, 8, false)]
    [InlineData("ally.clockwork", "Control", "Dormant", true, true, 8, false)]
    public void GloomBeastUsesItsVerifiedWakeToIdleWithoutWakingOtherDormantBosses(string asset, string fsm, string state, bool transition, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, transition, enemy, distance, asset, fsm));

    [Theory]
    [InlineData("spawn.seth", "Control", "Dormant", true, false, 8, true)]
    [InlineData("spawn.seth", "Control", "Dormant", true, true, 1, true)]
    [InlineData("spawn.seth", "Control", "Dormant", true, false, 1, false)]
    [InlineData("spawn.seth", "Control", "Dormant", false, true, 8, false)]
    [InlineData("spawn.seth", "Other", "Dormant", true, true, 8, false)]
    [InlineData("spawn.seth", "Control", "Wake Antic", true, true, 8, false)]
    [InlineData("spawn.coral-king", "Control", "Dormant", true, true, 8, false)]
    public void SethUsesItsOwnWakeAnticEntranceWithoutStartingUnrelatedDormantBosses(string asset, string fsm, string state, bool transition, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, transition, enemy, distance, asset, fsm));

    [Theory]
    [InlineData("spawn.seth", "Control", "Idle", true)]
    [InlineData("spawn.seth", "Control", "Dormant", false)]
    [InlineData("spawn.seth", "Control", "Attack Choice", false)]
    [InlineData("spawn.seth", "Control", "Evade", false)]
    [InlineData("spawn.seth", "Control", "Roar", false)]
    [InlineData("spawn.seth", "Other", "Idle", false)]
    [InlineData("ally.clockwork", "Control", "Idle", false)]
    public void SethWithoutNativeWalkUsesLowSpeedIdleHoverOnlyAtItsNeutralIdle(string asset, string fsm, string state, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.UsesStationaryHoverFollow(asset, fsm, state));


    /// <summary>尖角碎裂者隐枝形态用自己的AWAKE出场，不能在无敌随行时一直当枝条。</summary>
    /// <param name="asset">目录身份。</param>
    /// <param name="fsm">原作状态机。</param>
    /// <param name="state">原作当前状态。</param>
    /// <param name="wake">是否具有已核对的原出场转换。</param>
    /// <param name="enemy">是否发现敌人。</param>
    /// <param name="distance">与主播的距离。</param>
    /// <param name="expected">是否允许原出场事件。</param>
    [Theory]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Hidden", true, false, 8, true)]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Hidden", true, true, 1, true)]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Hidden", true, false, 2, false)]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Hidden", false, false, 8, false)]
    [InlineData("spawn.stick-insect-charger", "Attack", "Hidden", true, false, 8, false)]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Burst Antic", true, false, 8, false)]
    [InlineData("spawn.stick-insect-charger", "Behaviour Base", "Shake", true, false, 8, false)]
    [InlineData("spawn.bone-flyer-giant", "Behaviour Base", "Hidden", true, true, 8, false)]
    public void HiddenBranchWalkerUsesOnlyItsOwnVerifiedAwakeEntrance(string asset, string fsm, string state, bool wake, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, wake, enemy, distance, asset, fsm));

    /// <summary>塞斯的武器和阶段Idle不得在主控制器出招时接管随行，其余既有单位不变。</summary>
    /// <param name="asset">目录标识。</param>
    /// <param name="fsm">原作控制器。</param>
    /// <param name="root">是否在本体根节点。</param>
    /// <param name="expected">是否可参与本体随行。</param>
    [Theory]
    [InlineData("spawn.seth", "Control", true, true)]
    [InlineData("spawn.seth", "Stun", true, false)]
    [InlineData("spawn.seth", "Phase Control", true, false)]
    [InlineData("spawn.seth", "Circle Slash Catch", true, false)]
    [InlineData("spawn.seth", "Control", false, false)]
    [InlineData("spawn.seth", "FSM", false, false)]
    [InlineData("spawn.bone-hunter-chief", "Control", true, true)]
    [InlineData("spawn.pilgrim-03", "Attack", false, true)]
    [InlineData("ally.clockwork", "Control", true, true)]
    public void SethLocomotionCannotSuspendAuxiliaryIdleWhileMainControllerAttacks(string asset, string fsm, bool root, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.CanUseLocomotionController(asset, fsm, root));

    /// <summary>失缚怨魂只用已核对的Dormant原作WAKE出场，不唤醒其他阶段或其他Boss。</summary>
    /// <param name="asset">目录身份。</param>
    /// <param name="fsm">原作控制器。</param>
    /// <param name="state">当前状态。</param>
    /// <param name="wake">是否具有原作苏醒转换。</param>
    /// <param name="enemy">是否存在有效敌人。</param>
    /// <param name="distance">主播距离。</param>
    /// <param name="expected">是否允许苏醒。</param>
    [Theory]
    [InlineData("spawn.conductor-boss", "Control", "Dormant", true, false, 8, true)]
    [InlineData("spawn.conductor-boss", "Control", "Dormant", true, true, 1, true)]
    [InlineData("spawn.conductor-boss", "Control", "Dormant", true, false, 1, false)]
    [InlineData("spawn.conductor-boss", "Control", "Dormant", false, true, 8, false)]
    [InlineData("spawn.conductor-boss", "Custom Stun Control", "Dormant", true, true, 8, false)]
    [InlineData("spawn.conductor-boss", "Control", "Under", true, true, 8, false)]
    [InlineData("spawn.giant-centipede", "Control", "Dormant", true, true, 8, false)]
    public void ConductorUsesOnlyItsVerifiedOriginalWakeEntrance(string asset, string fsm, string state, bool wake, bool enemy, float distance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldWakeForCompanion(state, wake, enemy, distance, asset, fsm));

    /// <summary>空闲塞斯没有敌人时不能因“不在Throw Range”立即传送开战，原招收尾和有敌行为不变。</summary>
    /// <param name="asset">目录身份。</param>
    /// <param name="fsm">原作控制器。</param>
    /// <param name="state">原作状态。</param>
    /// <param name="range">原作范围。</param>
    /// <param name="ev">原感知事件。</param>
    /// <param name="enemy">是否有敌人。</param>
    /// <param name="expected">是否允许原事件。</param>
    [Theory]
    [InlineData("spawn.seth", "Control", "Idle", "Throw Range", "TELE OUT", false, false)]
    [InlineData("spawn.seth", "Control", "Idle", "Throw Range", "TELE OUT", true, true)]
    [InlineData("spawn.seth", "Control", "Throw", "Throw Range", "TELE OUT", false, true)]
    [InlineData("spawn.seth", "Control", "Idle", "Throw Range", "END", false, true)]
    [InlineData("spawn.seth", "Control", "Idle", "Above Range", "BLOCK UP", false, true)]
    [InlineData("spawn.seth", "Other", "Idle", "Throw Range", "TELE OUT", false, true)]
    [InlineData("spawn.conductor-boss", "Control", "Idle", "Throw Range", "TELE OUT", false, true)]
    [InlineData("ally.clockwork", "Control", "Idle", "Throw Range", "TELE OUT", false, true)]
    public void NoEnemyIsNotAnOutOfRangeTargetForSethIdleTeleport(string asset, string fsm, string state, string range, string ev, bool enemy, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldEmitNamedRangeEvent(asset, fsm, state, range, ev, enemy));
    /// <summary>巨型螺蝇只沿自身Dormant到Start Pause的出场链苏醒，不模拟攻击或波次结束。</summary>
    /// <param name="asset">召唤目录身份。</param>
    /// <param name="fsm">原作控制器。</param>
    /// <param name="state">当前原作状态。</param>
    /// <param name="entrance">是否验证原作出场转换。</param>
    /// <param name="expected">是否发送原作出场事件。</param>
    [Theory]
    [InlineData("spawn.coral-conch-driller-giant", "Control", "Dormant", true, true)]
    [InlineData("spawn.coral-conch-driller-giant", "Control", "Dormant", false, false)]
    [InlineData("spawn.coral-conch-driller-giant", "Control", "Away", true, false)]
    [InlineData("spawn.coral-conch-driller-giant", "Other", "Dormant", true, false)]
    [InlineData("spawn.giant-centipede", "Control", "Dormant", true, false)]
    [InlineData("spawn.hunter-queen", "Control", "BG Dance", true, false)]
    [InlineData("ally.clockwork", "Control", "Dormant", true, false)]
    public void ConchUsesOnlyItsVerifiedOriginalBattleStartEntrance(string asset, string fsm, string state, bool entrance, bool expected)
        => Assert.Equal(expected, CompanionCombatPolicy.ShouldStartCompanionEntrance(asset, fsm, state, entrance));

}
