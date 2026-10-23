#if LIVEINTERACT_QA
using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>仅测试编译存在的主线程验收入口；真实存档重定向到独立目录，正式包不含该类型。</summary>
internal sealed partial class QaHarness
{
    private readonly BaseUnityPlugin owner;
    private readonly GameActions actions;
    private readonly ModPipeClient bridge;
    private readonly Harmony protection = new("cn.liveinteract.qa.protection");
    private string isolatedRoot = "";
    private Vector3? anchor;
    private bool isolated;
    private static readonly List<JObject> damageLog = new();
    private static readonly List<JObject> actorEvents=new();
    // 按请求启用的原作动作只读记录，与命中验收日志分开保存。
    private static bool nativeActionTraceEnabled;
    private static readonly List<JObject> nativeActionTrace = new();
    internal static void RecordActor(GameObject actor,string kind)
    {
        var health=actor.GetComponentInChildren<HealthManager>();
        actorEvents.Add(new JObject{["Kind"]=kind,["Components"]=new JArray(actor.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name)),["Id"]=actor.GetComponent<OwnedSummon>()?.GetInstanceID()??0,["Name"]=actor.name,["Frame"]=Time.frameCount,["Time"]=Time.realtimeSinceStartup,["Active"]=actor.activeInHierarchy,["Hp"]=health?.hp,["Dead"]=health?.isDead});
        if(actorEvents.Count>200)actorEvents.RemoveAt(0);
    }
    private GameObject? hitSource;
    public bool IsIsolated => isolated;

    /// <summary>建立显式请求的 QA 控制器，不自动操作当前存档。</summary>
    public QaHarness(BaseUnityPlugin owner, GameActions actions, ModPipeClient bridge)
    {
        this.owner = owner; this.actions = actions; this.bridge = bridge;
    }

    /// <summary>只处理本机构造的验收请求。</summary>
    public async void Handle(string json)
    {
        var command = JObject.Parse(json); var id = (string)command["Id"]!;
        var result = new ActionResult { RequestId = id };
        try
        {
            var operation = (string?)command["Operation"] ?? "state";
            switch (operation)
            {
                case "screening-spawn":
                    RequireIsolation();
                    result.Message = await actions.ScreenSummonAsync(this, (string)command["MonsterId"]!, (bool?)command["Friendly"] ?? true, (float?)command["X"] ?? -6);
                    result.Success = true; bridge.Send(new() { Kind = "result", Result = result }); return;
                case "isolate": Isolate(); break;
                case "new-game":
                    RequireIsolation(); GameManager.instance.profileID = 4; GameManager.instance.StartNewGame(false, false); break;
                case "load-game":
                    RequireIsolation(); GameManager.instance.profileID = 4; GameManager.instance.LoadGameFromUI(4); break;
                case "go":
                    RequireIsolation(); GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = (string?)command["Scene"] ?? "Tut_01", EntryGateName = (string?)command["Gate"] ?? "", EntryDelay = 0, PreventCameraFadeOut = true }); break;
                case "fixture-safe-enemy":
                    RequireIsolation();
                    foreach (var enemy in UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None).Where(e => e.name.Contains("[LiveInteract]")))
                    {
                        foreach (var hazard in enemy.GetComponentsInChildren<DamageHero>(true)) { hazard.enabled = false; }
                    }
                    break;
                case "probe-companions":
                    RequireIsolation();
                    var companions = Resources.FindObjectsOfTypeAll<ClockworkHatchling>();
                    var probe = new JObject { ["Count"] = companions.Length, ["Items"] = new JArray(companions.Take(12).Select(item => new JObject
                    {
                        ["Name"] = item.name, ["Parent"] = item.transform.parent != null ? item.transform.parent.name : "",
                        ["Active"] = item.gameObject.activeInHierarchy, ["Scene"] = item.gameObject.scene.name,
                        ["Damage"] = item.normalDetails.damage, ["State"] = item.CurrentState.ToString(),
                        ["HasToolSource"] = (bool)AccessTools.Field(typeof(ClockworkHatchling), "hasToolSource").GetValue(item),
                        ["ToolSource"] = (AccessTools.Field(typeof(ClockworkHatchling), "toolSource").GetValue(item) as ToolItem)?.name,
                        ["DamageHeroCount"] = item.GetComponentsInChildren<DamageHero>(true).Length,
                        ["Animations"] = new JArray(item.GetComponentsInChildren<tk2dSpriteAnimator>(true).Select(a => a.Library != null ? a.Library.name : ""))
                    })) };
                    result.Success = true; result.Message = probe.ToString(Formatting.None); bridge.Send(new() { Kind = "result", Result = result }); return;
                case "journal-names":
                    result.Success=true;result.Message=new JArray(Resources.FindObjectsOfTypeAll<EnemyJournalRecord>().Select(j=>new JObject{["Id"]=j.name,["Name"]=j.DisplayName.ToString()})).ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "silence-originals":
                    RequireIsolation();
                    foreach(var nativeActor in UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None))
                        if(nativeActor.GetComponentInParent<OwnedSummon>()==null)nativeActor.gameObject.SetActive(false);
                    HeroController.SilentInstance.RelinquishControl();break;
                case "arena":
                    RequireIsolation(); Prepare(true); HeroController.SilentInstance.RelinquishControl(); damageLog.Clear();actorEvents.Clear();
                    foreach (var actor in UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None))
                    { if(actor.GetComponentInParent<OwnedSummon>()==null)actor.gameObject.SetActive(false); }
                    break;
                case "clear-damage-log": RequireIsolation(); damageLog.Clear(); break;
                case "native-set-state":
                    RequireIsolation();var setActor=Resources.FindObjectsOfTypeAll<OwnedSummon>().First(a=>a.gameObject.scene.IsValid()&&a.GetInstanceID()==(int)command["ActorId"]!);
                    var setFsm=setActor.GetComponentsInChildren<PlayMakerFSM>(true).First(f=>f.FsmName==(string)command["Fsm"]!);
                    var requestedState=(string)command["State"]!;if(!setFsm.FsmStates.Any(state=>state.Name==requestedState))throw new InvalidOperationException("不存在的原作状态");
                    foreach(var renderer in setActor.GetComponents<Renderer>())renderer.enabled=true;
                    foreach(var collider in setActor.GetComponents<Collider2D>())collider.enabled=true;
                    setFsm.SetState(requestedState);break;
                case "scope-lifetime-proof":
                    RequireIsolation();
                    var originProbe=new GameObject("QA expired origin");
                    var runnerProbe=new GameObject("QA independent native-effect runner");
                    var probeRoutine=runnerProbe.AddComponent<QaRoutineProbe>();
                    runnerProbe.AddComponent<NativeAllyProjectile>().Assign(null,originProbe,true);
                    var previousContext=NativeAllyExecution.Capture();
                    NativeAllyExecution.Restore(new NativeCallState{Origin=originProbe,Spawned=true,Friendly=true});
                    try{probeRoutine.StartCoroutine(probeRoutine.Run());}
                    finally{NativeAllyExecution.Restore(previousContext);}
                    UnityEngine.Object.Destroy(originProbe);
                    await Task.Delay(220);
                    var scopeResult=new JObject{["Steps"]=probeRoutine.Steps,["ExpectedSteps"]=3,
                        ["StayedFriendly"]=probeRoutine.StayedFriendly,["TargetedHero"]=probeRoutine.TargetedHero,["OriginDestroyed"]=originProbe==null,
                        ["RunnerAlive"]=runnerProbe!=null};
                    UnityEngine.Object.Destroy(runnerProbe);
                    result.Success=true;result.Message=scopeResult.ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "native-action-trace":
                    RequireIsolation();
                    if (command["Enabled"] != null) { nativeActionTraceEnabled = (bool)command["Enabled"]!; }
                    if ((bool?)command["Clear"] == true) { nativeActionTrace.Clear(); }
                    result.Success = true;
                    result.Message = new JObject { ["Enabled"] = nativeActionTraceEnabled,
                        ["Actions"] = new JArray(nativeActionTrace) }.ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "wall-probe-config":
                    RequireIsolation();
                    if(command["Enabled"]!=null)FriendlyTerrainProbeHook.Enabled=(bool)command["Enabled"]!;
                    result.Success=true;result.Message=new JObject{["Enabled"]=FriendlyTerrainProbeHook.Enabled}.ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "native-ray-state":
                    RequireIsolation();
                    var rayActor=Resources.FindObjectsOfTypeAll<OwnedSummon>().First(a=>a.GetInstanceID()==(int)command["ActorId"]!);
                    var rayCache=AccessTools.Field(typeof(PlayMakerUnity2d),"lastRaycastHit2DInfoLUT").GetValue(null) as Dictionary<Fsm,RaycastHit2D>;
                    result.Success=true;
                    result.Message=new JArray(rayActor.GetComponentsInChildren<PlayMakerFSM>(true).Select(f=>
                    {
                        var found=rayCache!=null&&rayCache.TryGetValue(f.Fsm,out var unused);
                        var hit=found?rayCache![f.Fsm]:default;var collider=hit.collider;
                        return new JObject{["Fsm"]=f.FsmName,["State"]=f.ActiveStateName,["Recorded"]=found,
                            ["Collider"]=collider!=null?Hierarchy(collider.transform):null,
                            ["Layer"]=collider!=null?(int?)collider.gameObject.layer:null,
                            ["Health"]=collider!=null?collider.GetComponentInParent<HealthManager>()?.name:null,
                            ["OwnedRoot"]=collider!=null?collider.GetComponentInParent<OwnedSummon>()?.GetInstanceID():null,
                            ["PointX"]=hit.point.x,["PointY"]=hit.point.y,["Distance"]=hit.distance};
                    })).ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "native-state-detail":
                    RequireIsolation();
                    var detailActor=Resources.FindObjectsOfTypeAll<OwnedSummon>()
                        .First(a=>a.GetInstanceID()==(int)command["ActorId"]!);
                    result.Success=true;
                    result.Message=new JArray(detailActor.GetComponentsInChildren<PlayMakerFSM>(true)
                        .Where(f=>command["Fsm"]==null||f.FsmName==(string)command["Fsm"]!)
                        .Select(f=>new JObject
                        {
                            ["Name"]=f.FsmName,["Enabled"]=f.enabled,["ObjectActive"]=f.gameObject.activeInHierarchy,
                            ["State"]=f.ActiveStateName,["InspectedState"]=(string?)command["State"]??f.ActiveStateName,
                            ["Path"]=Hierarchy(f.transform),
                            ["ObjectVariables"]=new JObject(f.FsmVariables.GameObjectVariables.GroupBy(v=>v.Name)
                                .Select(g=>new JProperty(g.Key,g.First().Value!=null?new JObject{
                                    ["Name"]=g.First().Value.name,["Id"]=g.First().Value.GetInstanceID(),
                                    ["Path"]=Hierarchy(g.First().Value.transform),["Scene"]=g.First().Value.scene.name}:null))),
                            ["Variables"]=JObject.FromObject(f.FsmVariables.FloatVariables.GroupBy(v=>v.Name).ToDictionary(g=>g.Key,g=>g.First().Value)),
                            ["Bools"]=JObject.FromObject(f.FsmVariables.BoolVariables.GroupBy(v=>v.Name).ToDictionary(g=>g.Key,g=>g.First().Value)),
                            ["Actions"]=new JArray((f.FsmStates.FirstOrDefault(st=>st.Name==((string?)command["State"]??f.ActiveStateName))?.Actions??Array.Empty<FsmStateAction>())
                                .Select(a=>new JObject{["Type"]=a.GetType().Name,["Enabled"]=a.Enabled,
                                    ["Entered"]=a.Entered,["Active"]=a.Active,["Finished"]=a.Finished,
                                    ["RayProbe"]=a is HutongGames.PlayMaker.Actions.RayCast2dV2 ray ? QaTerrainProfile.Read(ray,detailActor) : null,
                                    // 值名称可能相同，但Next Point与Patrol Point并非同一变量；只读保留绑定身份。
                                    ["ObjectBindings"]=new JObject(a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public)
                                        .Where(field=>field.FieldType==typeof(FsmGameObject)).Select(field=>
                                        {
                                            var value=(FsmGameObject?)field.GetValue(a);
                                            return new JProperty(field.Name,value==null?null:new JObject{
                                                ["VariableName"]=value.Name,
                                                ["LocalVariable"]=f.FsmVariables.GameObjectVariables.Any(v=>ReferenceEquals(v,value)),
                                                ["ValueName"]=value.Value!=null?value.Value.name:null,
                                                ["ValueId"]=value.Value!=null?(int?)value.Value.GetInstanceID():null});
                                        })),["FloatBindings"]=new JObject(a.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public)
                                        .Where(field=>field.FieldType==typeof(FsmFloat)).Select(field=>
                                        {
                                            var value=(FsmFloat?)field.GetValue(a);
                                            return new JProperty(field.Name,value==null?null:new JObject{
                                                ["VariableName"]=value.Name,["Value"]=value.Value,
                                                ["UseVariable"]=value.UseVariable,
                                                ["LocalVariable"]=f.FsmVariables.FloatVariables.Any(v=>ReferenceEquals(v,value))});
                                        })),["Fields"]=new JObject(a.GetType()
                                    .GetFields(BindingFlags.Instance|BindingFlags.Public)
                                    .Select(field=>new JProperty(field.Name,DescribeFsmValue(field.GetValue(a)))))}))
                        })).ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "native-states":
                    RequireIsolation();
                    result.Success=true;result.Message=new JArray(Resources.FindObjectsOfTypeAll<OwnedSummon>().Where(actor=>actor.gameObject.scene.IsValid()).Select(actor=>new JObject{["Id"]=actor.GetInstanceID(),["Actor"]=actor.name,["Fsms"]=new JArray(actor.GetComponentsInChildren<PlayMakerFSM>(true).Select(f=>new JObject{["Name"]=f.FsmName,["Active"]=f.ActiveStateName,["States"]=new JArray((f.FsmStates??Array.Empty<FsmState>()).Select(state=>new JObject{["Name"]=state.Name,["Transitions"]=new JArray(state.Transitions.Select(t=>new JObject{["Event"]=t.EventName,["To"]=t.ToState})),["Actions"]=new JArray((state.Actions??Array.Empty<FsmStateAction>()).Select(a=>a.GetType().Name))}))}))})).ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "probe-contacts":
                    RequireIsolation();var probes=new JArray();
                    foreach(var fighter in UnityEngine.Object.FindObjectsByType<NativeEnemyAlly>(FindObjectsSortMode.None))
                    foreach(var weapon in fighter.GetComponentsInChildren<DamageHero>(true).Where(w=>w.enabled&&w.gameObject.activeInHierarchy))
                    foreach(var box in weapon.GetComponents<Collider2D>().Where(c=>c.enabled))
                    {
                        var touched=new List<Collider2D>();box.Overlap(new ContactFilter2D{useTriggers=true,useLayerMask=false},touched);
                        var row=new JObject{["Weapon"]=weapon.name,["Collider"]=box.name,["Overlap"]=new JArray(touched.Select(c=>new JObject{["Collider"]=c.name,["Layer"]=c.gameObject.layer,["Health"]=c.GetComponentInParent<HealthManager>()?.name})),["WeaponDamage"]=weapon.damageDealt};
                        var victim=UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).FirstOrDefault(c=>c.GetComponent<OwnedCompanion>()==null)?.GetComponentInChildren<HealthManager>();
                        if(victim!=null){row["BeforeInjectedHit"]=victim.hp;victim.Hit(new HitInstance{Source=weapon.gameObject,DamageDealt=1,AttackType=AttackTypes.Generic,Multiplier=1,MagnitudeMultiplier=1,IsFirstHit=true,IsHeroDamage=false});row["AfterInjectedHit"]=victim.hp;}
                        probes.Add(row);
                    }
                    result.Success=true;result.Message=probes.ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "fixture-dummy":
                    RequireIsolation();
                    var dummy=UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).First(a=>a.GetComponent<OwnedCompanion>()==null&&((int?)command["EnemyId"]==null||a.GetInstanceID()==(int)command["EnemyId"]!));
                    var allyPosition=UnityEngine.Object.FindObjectsByType<NativeEnemyAlly>(FindObjectsSortMode.None).FirstOrDefault()?.transform.position;
                    var attachment=dummy.GetComponent<QaContactTarget>()??dummy.gameObject.AddComponent<QaContactTarget>();
                    if(allyPosition.HasValue)
                    {
                        dummy.transform.position=allyPosition.Value;
                        attachment.Subject=UnityEngine.Object.FindObjectsByType<NativeEnemyAlly>(FindObjectsSortMode.None).First().transform;
                    }
                    dummy.GetComponentInChildren<HealthManager>().hp=1000;
                    foreach(var script in dummy.GetComponentsInChildren<MonoBehaviour>(true))
                        if(script!=null&&(script is PlayMakerFSM||script.GetType().Assembly==typeof(HealthManager).Assembly)&&script is not HealthManager&&script is not tk2dSpriteAnimator)script.enabled=false;
                    foreach(var collider in dummy.GetComponentsInChildren<Collider2D>(true))if(collider.gameObject==dummy.gameObject)collider.enabled=true;
                    var dummyBody=dummy.GetComponent<Rigidbody2D>();if(dummyBody!=null){dummyBody.linearVelocity=Vector2.zero;dummyBody.bodyType=RigidbodyType2D.Static;}
                    Physics2D.SyncTransforms();break;
                case "combat-dummy":
                    RequireIsolation();
                    var combatDummy=UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None)
                        .First(a=>a.GetComponent<OwnedCompanion>()==null&&((int?)command["EnemyId"]==null||a.GetInstanceID()==(int)command["EnemyId"]!));
                    foreach(var script in combatDummy.GetComponentsInChildren<MonoBehaviour>(true))
                        if(script!=null&&(script is PlayMakerFSM||script.GetType().Assembly==typeof(HealthManager).Assembly)
                            &&script is not HealthManager&&script is not tk2dSpriteAnimator)script.enabled=false;
                    foreach(var weapon in combatDummy.GetComponentsInChildren<DamageHero>(true))weapon.damageDealt=0;
                    combatDummy.GetComponentInChildren<HealthManager>().hp=Math.Max(1, Math.Min(100000, (int?)command["Hp"] ?? 1000));
                    combatDummy.transform.position=HeroController.SilentInstance.transform.position+new Vector3((float?)command["X"]??6,(float?)command["Y"]??0,0);
                    var dummyRb=combatDummy.GetComponent<Rigidbody2D>();
                    if(dummyRb!=null){dummyRb.linearVelocity=Vector2.zero;dummyRb.constraints=RigidbodyConstraints2D.FreezeAll;}
                    Physics2D.SyncTransforms();break;
                case "hero-regain":
                    // 场地初始化会收回主播操作；模拟挥针前恢复输入，不重置召唤或生命。
                    RequireIsolation(); HeroController.SilentInstance.RegainControl(); HeroController.SilentInstance.AcceptInput(); break;
                case "hero-heal":
                    // 敌方筛测给主播补满生命，避免低血基线在验证击杀前被打死；不清场。
                    RequireIsolation(); PlayerData.instance.health = PlayerData.instance.maxHealth; break;
                case "hero-fsm":
                    // 读取主角状态机当前状态及出口；给出 State 时复位，用于清除上一单位遗留的抓取锁。
                    RequireIsolation(); var heroFsm=HeroController.SilentInstance.GetComponents<PlayMakerFSM>().First(f=>f.FsmName==(string)command["Fsm"]!);
                    var resetState=(string?)command["State"];if(resetState!=null){if(!heroFsm.FsmStates.Any(state=>state.Name==resetState))throw new InvalidOperationException("不存在的原作状态");heroFsm.SetState(resetState);}
                    result.Success=true;result.Message=new JObject{["State"]=heroFsm.ActiveStateName,
                        ["Global"]=new JArray(heroFsm.FsmGlobalTransitions.Select(t=>t.EventName+"->"+t.ToState)),
                        ["Transitions"]=new JArray(heroFsm.FsmStates.Select(s=>new JObject{["Name"]=s.Name,["Out"]=new JArray(s.Transitions.Select(t=>t.EventName+"->"+t.ToState))}))}.ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "hero-attack":
                    RequireIsolation();
                    // 调用玩家普通挥针入口，保留原生武器窗口、碰撞与伤害；不直接命中敌人。
                    var attackingHero=HeroController.SilentInstance;
                    if((bool?)command["Right"]??true)attackingHero.FaceRight();else attackingHero.FaceLeft();
                    AccessTools.Method(typeof(HeroController),"DoAttack").Invoke(attackingHero,null);
                    break;
                case "friendly-hero-collisions":
                    RequireIsolation(); result.Success = true;
                    result.Message = ReadFriendlyHeroCollisions().ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "friendly-collision-probe":
                    RequireIsolation(); result.Success = true;
                    result.Message = ProbeFriendlyCollisionPairs().ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "boundary-benchmark":
                    RequireIsolation(); result.Success=true;result.Message=QaBoundaryBenchmark.Run().ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "load-isolation":
                    RequireIsolation(); owner.StartCoroutine(RunLoadIsolation(result)); return;
                case "runtime-profile-start":
                    RequireIsolation(); QaRuntimeTimings.Start(); break;
                case "runtime-profile-state":
                case "runtime-profile-stop":
                    RequireIsolation(); result.Success = true;
                    result.Message = QaRuntimeTimings.Snapshot().ToString(Formatting.None);
                    if (operation == "runtime-profile-stop") { QaRuntimeTimings.Stop(); }
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                // 只读碰撞现场：区分召唤残留、原场景地形和测试角色定位错误。
                case "hero-collision-state":
                    RequireIsolation(); var probeHero = HeroController.SilentInstance;
                    result.Success = true;
                    result.Message = new JObject {
                        ["Hero"] = Snapshot()["Hero"],
                        ["MotionFields"] = JObject.FromObject(typeof(HeroController).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Where(f => (f.Name.IndexOf("recoil", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("conveyor", StringComparison.OrdinalIgnoreCase) >= 0) && f.FieldType.IsPrimitive)
                            .ToDictionary(f => f.Name, f => f.GetValue(probeHero))),
                        ["HeroFsms"] = new JArray(probeHero.GetComponentsInChildren<PlayMakerFSM>(true).Select(f => new JObject { ["Path"] = Hierarchy(f.transform), ["Fsm"] = f.FsmName, ["State"] = f.ActiveStateName })),
                        ["Pushers"] = new JArray(Resources.FindObjectsOfTypeAll<FollowTransform>().Where(f => f.gameObject.scene.IsValid() && f.name == "Hero Physics Pusher")
                            .Select(f => new JObject { ["Path"] = Hierarchy(f.transform), ["Target"] = f.CurrentTarget != null ? Hierarchy(f.CurrentTarget) : null,
                                ["X"] = f.transform.position.x, ["Y"] = f.transform.position.y,
                                ["OffsetX"] = f.Offset.x, ["OffsetY"] = f.Offset.y,
                                ["CollisionsWithHero"] = new JArray(f.GetComponents<Collider2D>().SelectMany(a => probeHero.GetComponents<Collider2D>().Select(h => new JObject { ["Ignored"] = Physics2D.GetIgnoreCollision(a, h) }))) })),
                        ["NearbyColliders"] = new JArray(Physics2D.OverlapBoxAll(probeHero.transform.position, new Vector2(5, 5), 0)
                            .Select(c => new JObject { ["Path"] = Hierarchy(c.transform), ["Type"] = c.GetType().Name,
                                ["Layer"] = LayerMask.LayerToName(c.gameObject.layer), ["Trigger"] = c.isTrigger,
                                ["MinX"] = c.bounds.min.x, ["MaxX"] = c.bounds.max.x,
                                ["MinY"] = c.bounds.min.y, ["MaxY"] = c.bounds.max.y,
                                ["Owned"] = c.GetComponentInParent<OwnedSummon>() != null,
                                ["Resource"] = c.GetComponentInParent<OwnedTransientResource>()?.AssetKey,
                                ["Components"] = new JArray(c.GetComponents<Component>().Where(v => v != null).Select(v => v.GetType().Name)) }))
                    }.ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "resource-state":
                    RequireIsolation(); result.Success=true;
                    result.Message=new JObject{["Flow"]=NativeAllyExecution.InSpawnedFlow,["FriendlyFlow"]=NativeAllyExecution.IsFriendlyFlow,
                        ["Resources"]=new JArray(Resources.FindObjectsOfTypeAll<OwnedTransientResource>()
                            .Where(resource=>resource!=null&&resource.gameObject.scene.IsValid()).Select(resource=>new JObject{
                                ["Id"]=resource.gameObject.GetInstanceID(),["Path"]=Hierarchy(resource.transform),["Asset"]=resource.AssetKey,
                                ["Friendly"]=resource.Friendly,["Active"]=resource.gameObject.activeInHierarchy,
                                ["Scene"]=resource.gameObject.scene.name,["Components"]=new JArray(resource.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name))}))}.ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "save-private":
                    RequireIsolation();
                    if(GameManager.instance.profileID!=4||!actions.CanAct)throw new InvalidOperationException("Only controllable isolated profile 4 can be saved");
                    var savedPrivate=new TaskCompletionSource<bool>();GameManager.instance.SaveGame(ok=>savedPrivate.TrySetResult(ok));
                    if(await Task.WhenAny(savedPrivate.Task,Task.Delay(15000))!=savedPrivate.Task||!await savedPrivate.Task)
                        throw new InvalidOperationException("Private fixture save did not complete");
                    break;
                case "hero-combat-state":
                    RequireIsolation(); result.Success = true;
                    result.Message = QaRuntimeTimings.HeroState().ToString(Formatting.None);
                    bridge.Send(new() { Kind = "result", Result = result }); return;
                case "follow-state":
                    RequireIsolation();
                    result.Success=true;result.Message=FollowSnapshot().ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "follow-sample":
                    RequireIsolation();var samples=new JArray();
                    for(var sample=0;sample<Math.Min(80,(int?)command["Count"]??20);sample++)
                    {samples.Add(FollowSnapshot());await Task.Delay((int?)command["IntervalMs"]??200);}
                    result.Success=true;result.Message=samples.ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "move-hero":
                    RequireIsolation();var movingHero=HeroController.SilentInstance;
                    movingHero.transform.position+=new Vector3((float?)command["X"]??0,(float?)command["Y"]??0,0);
                    movingHero.GetComponent<Rigidbody2D>().linearVelocity=Vector2.zero;
                    if ((bool?)command["SyncPhysics"] == true)
                    {
                        movingHero.GetComponent<Rigidbody2D>().position = movingHero.transform.position;
                        var heroPusher = AccessTools.Field(typeof(HeroController), "heroPhysicsPusher").GetValue(movingHero) as GameObject;
                        heroPusher?.GetComponent<FollowTransform>()?.ForceFollowUpdate();
                        if (heroPusher != null && heroPusher.GetComponent<Rigidbody2D>() is {} pusherBody)
                        { pusherBody.position = heroPusher.transform.position; pusherBody.linearVelocity = Vector2.zero; }
                    }
                    Physics2D.SyncTransforms();break;
                case "move-actor":
                    RequireIsolation();
                    var actorId=(int?)command["ActorId"] ?? 0;
                    var selected=UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).First(a=>actorId==0||a.GetInstanceID()==actorId);
                    selected.transform.position=HeroController.SilentInstance.transform.position+new Vector3((float?)command["X"]??2,(float?)command["Y"]??0,0);
                    var rb=selected.GetComponent<Rigidbody2D>();if(rb!=null){rb.position=selected.transform.position;rb.linearVelocity=Vector2.zero;}Physics2D.SyncTransforms();
                    // 可选同帧回读，避免快速单位在下一次网络采样前已经正常前进而误报出生传送。
                    if((bool?)command["ReturnFollowState"]==true)
                    {result.Success=true;result.Message=FollowSnapshot().ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;}
                    break;
                case "actor-hit":
                    RequireIsolation();
                    var hitId=(int?)command["ActorId"]??0;
                    var hitActor=UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).First(a=>hitId==0||a.GetInstanceID()==hitId);
                    var targetHealth=hitActor.GetComponentInChildren<HealthManager>();
                    if(hitSource==null){hitSource=new GameObject("QA hostile damage source");hitSource.AddComponent<DamageHero>();}
                    var source=(bool?)command["HeroSource"]==true?HeroController.SilentInstance.gameObject:hitSource;
                    targetHealth.Hit(new HitInstance { Source=source,DamageDealt=(int?)command["Damage"]??1,Multiplier=1,MagnitudeMultiplier=1,AttackType=AttackTypes.Generic,IsHeroDamage=(bool?)command["HeroSource"]==true });
                    await Task.Delay((int?)command["WaitMs"]??150);
                    break;
                case "prepare":
                    RequireIsolation(); Prepare((bool?)command["Unlock"] ?? false); break;
                case "warp":
                    RequireIsolation();
                    await actions.ExecuteAsync(new ActionRequest { ActionId = "scene.warp", AllowPersistent = true, Arguments = new() { ["scene"] = (string)command["Scene"]!, ["gate"] = (string?)command["Gate"] ?? "" } }); break;
                case "mark-anchor": anchor = HeroController.SilentInstance.transform.position; break;
                case "restore-position":
                    RequireIsolation(); HeroController.SilentInstance.transform.position = anchor ?? HeroController.SilentInstance.transform.position;
                    HeroController.SilentInstance.GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero; break;
                case "lock-tools":
                    RequireIsolation();
                    foreach (var tool in ToolItemManager.GetAllTools())
                    {
                        var property = AccessTools.Property(typeof(ToolItem), "SavedData"); var saved = property.GetValue(tool, null);
                        AccessTools.Field(saved.GetType(), "IsUnlocked").SetValue(saved, false); property.SetValue(tool, saved, null);
                    }
                    break;
                case "lock-crests":
                    RequireIsolation();
                    foreach (var crest in ToolItemManager.GetAllCrests())
                    {
                        var property = AccessTools.Property(typeof(ToolCrest), "SaveData"); var saved = property.GetValue(crest, null);
                        AccessTools.Field(saved.GetType(), "IsUnlocked").SetValue(saved, false); property.SetValue(crest, saved, null);
                    }
                    break;
                case "empty-tools":
                    RequireIsolation();
                    foreach (var tool in ToolItemManager.GetUnlockedTools())
                    {
                        var property = AccessTools.Property(typeof(ToolItem), "SavedData"); var saved = property.GetValue(tool, null);
                        AccessTools.Field(saved.GetType(), "AmountLeft").SetValue(saved, 0); property.SetValue(tool, saved, null);
                    }
                    break;
                case "use-air-counters":
                    RequireIsolation(); AccessTools.Field(typeof(HeroController), "airDashed").SetValue(HeroController.SilentInstance, true); AccessTools.Field(typeof(HeroController), "doubleJumped").SetValue(HeroController.SilentInstance, true); break;
                case "probe-move":
                    RequireIsolation(); AccessTools.Method(typeof(HeroController), "Move", new[] { typeof(float), typeof(bool) }).Invoke(HeroController.SilentInstance, new object[] { (float?)command["Axis"] ?? 1f, false }); break;
                case "probe-hit":
                    RequireIsolation();
                    var target = UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None).First(e => e.hp > 0 && !e.isDead && e.name.Contains("[LiveInteract]"));
                    var hero = HeroController.SilentInstance;
                    var kind = (string?)command["DamageKind"] ?? "nail";
                    var hit = new HitInstance { Source = hero.gameObject, AttackType = kind == "nail" ? AttackTypes.Nail : AttackTypes.Generic, DamageDealt = (int?)command["Damage"] ?? 10, Multiplier = 1, MagnitudeMultiplier = 1, IgnoreInvulnerable = true, IsManualTrigger = true };
                    if (kind == "tools") { hit.RepresentingTool = ToolItemManager.GetToolByName((string)command["Tool"]!); }
                    target.Hit(hit); break;
                case "reset": actions.Reset(); break;
                case "capture":
                    var directory = Path.Combine(RequireIsolation(), "captures"); Directory.CreateDirectory(directory);
                    ScreenCapture.CaptureScreenshot(Path.Combine(directory, id + ".png")); break;
                case "quit": owner.StartCoroutine(QuitAfterReply()); break;
                case "outline-config":
                    RequireIsolation();actions.FactionOutlineEnabled=(bool?)command["Enabled"]??true;actions.FactionOutlineWidth=SummonOutlineStyle.NormalizeWidth((double?)command["Width"]??2);
                    if(command["OrderOffset"] is JToken orderOffset)SummonFactionOutline.QaOrderOffset=(int)orderOffset;break;
                case "outline-fixture":
                    RequireIsolation();
                    foreach(var unit in UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None))
                    {
                        var friend=unit.GetComponent<OwnedCompanion>()!=null;
                        foreach(var script in unit.GetComponentsInChildren<MonoBehaviour>(true))
                            if(script is PlayMakerFSM||script is NativeEnemyAlly||script is tk2dSpriteAnimator||
                               script!=null&&script.GetType().Assembly==typeof(HealthManager).Assembly&&script is not tk2dBaseSprite&&script is not HealthManager)script.enabled=false;
                        unit.transform.position=HeroController.SilentInstance.transform.position+new Vector3(friend?-3.2f:3.2f,.1f,0);
                        foreach(var body in unit.GetComponentsInChildren<Rigidbody2D>(true)){body.linearVelocity=Vector2.zero;body.angularVelocity=0;body.constraints=RigidbodyConstraints2D.FreezeAll;}
                    }
                    Physics2D.SyncTransforms();break;
                case "label-state":
                    RequireIsolation();result.Success=true;
                    result.Message=new JArray(actions.SpawnLabels().Select(label=>new JObject
                    { ["Name"]=label.Name,["Friendly"]=label.IsFriendly,["X"]=label.Position.x,["Y"]=label.Position.y,
                      ["ScreenX"]=Camera.main.WorldToScreenPoint(label.Position).x,["ScreenY"]=Camera.main.WorldToScreenPoint(label.Position).y })).ToString(Formatting.None);
                    bridge.Send(new(){Kind="result",Result=result});return;
                case "scene-renderers":
                    RequireIsolation(); result.Success=true;
                    var rendererName=(string?)command["NameContains"]??"";
                    result.Message=new JArray(UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                        .Where(r=>r.enabled&&r.gameObject.activeInHierarchy&&r is not ParticleSystemRenderer&&r is not TrailRenderer
                            &&Vector2.Distance(r.bounds.center,HeroController.SilentInstance.transform.position)<25
                            &&r.name.IndexOf(rendererName,StringComparison.OrdinalIgnoreCase)>=0)
                        .OrderBy(r=>r.name).Take(800).Select(r=>new JObject{
                            ["Id"]=r.gameObject.GetInstanceID(),["Path"]=Hierarchy(r.transform),["Name"]=r.name,
                            ["X"]=r.bounds.center.x,["Y"]=r.bounds.center.y,["Width"]=r.bounds.size.x,["Height"]=r.bounds.size.y,
                            ["Summon"]=r.GetComponentInParent<OwnedSummon>()?.AssetKey,
                            ["Resource"]=r.GetComponentInParent<OwnedTransientResource>()?.AssetKey,
                            ["Friendly"]=NativeAllyExecution.IsFriendly(r.gameObject),
                            ["Shader"]=r.sharedMaterial?.shader?.name,
                            ["Components"]=new JArray(r.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name))
                        })).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "cached-prefab-combat-profile":
                    RequireIsolation();
                    var prefabId=(string)command["MonsterId"]!;
                    var cachedPrefab=actions.InspectCachedSummonPrefab(this,prefabId);
                    var prefabDefinition=MonsterCatalog.Find(prefabId)!;
                    result.Message=new JObject{
                        ["Id"]=prefabId,["SourceScene"]=prefabDefinition.SourceScene,["Bundle"]=prefabDefinition.Bundle,
                        ["Asset"]=prefabDefinition.Asset,["PrefabName"]=cachedPrefab.name,["LoadedPrefabOnly"]=true,
                        ["SceneValid"]=cachedPrefab.scene.IsValid(),
                        ["RootTransform"]=new JObject{
                            ["Position"]=new JObject{["X"]=cachedPrefab.transform.position.x,["Y"]=cachedPrefab.transform.position.y,["Z"]=cachedPrefab.transform.position.z},
                            ["EulerAngles"]=new JObject{["X"]=cachedPrefab.transform.eulerAngles.x,["Y"]=cachedPrefab.transform.eulerAngles.y,["Z"]=cachedPrefab.transform.eulerAngles.z},
                            ["LocalScale"]=new JObject{["X"]=cachedPrefab.transform.localScale.x,["Y"]=cachedPrefab.transform.localScale.y,["Z"]=cachedPrefab.transform.localScale.z}},
                        // 未激活原作资源的物理配置，和友军实例分开留证，不修改原资源。
                        ["Rigidbodies"]=new JArray(cachedPrefab.GetComponentsInChildren<Rigidbody2D>(true).Select(rb=>new JObject{
                            ["Path"]=Hierarchy(rb.transform),["Type"]=rb.bodyType.ToString(),["Constraints"]=rb.constraints.ToString(),
                            ["Simulated"]=rb.simulated,["Gravity"]=rb.gravityScale})),
                        ["Climbers"]=new JArray(cachedPrefab.GetComponentsInChildren<Climber>(true).Select(c=>new JObject{
                            ["Path"]=Hierarchy(c.transform),["EulerAngles"]=new JObject{["X"]=c.transform.eulerAngles.x,["Y"]=c.transform.eulerAngles.y,["Z"]=c.transform.eulerAngles.z},
                            ["SpriteFacesRight"]=c.SpriteFacesRight,["Speed"]=c.speed,["WalkAnim"]=c.WalkAnim,
                            ["WallBehaviour"]=c.WallBehaviour.ToString(),["EdgeBehaviour"]=c.EdgeBehaviour.ToString()})),
                        ["Components"]=new JArray(cachedPrefab.GetComponentsInChildren<MonoBehaviour>(true)
                            .Where(c=>c!=null).Select(c=>new JObject{["Path"]=Hierarchy(c.transform),["Type"]=c.GetType().FullName})),
                        ["DamageHero"]=new JArray(cachedPrefab.GetComponentsInChildren<DamageHero>(true)
                            .Select(c=>new JObject{["Path"]=Hierarchy(c.transform),["Damage"]=c.damageDealt,["Enabled"]=c.enabled})),
                        ["DamageEnemies"]=new JArray(cachedPrefab.GetComponentsInChildren<DamageEnemies>(true)
                            .Select(c=>Hierarchy(c.transform))),
                        ["ParticleDamageHero"]=new JArray(cachedPrefab.GetComponentsInChildren<ParticleDamageHero>(true)
                            .Select(c=>Hierarchy(c.transform))),
                        ["Health"]=new JArray(cachedPrefab.GetComponentsInChildren<HealthManager>(true)
                            .Select(c=>new JObject{["Path"]=Hierarchy(c.transform),["Hp"]=c.hp,["Invincible"]=c.IsInvincible})),
                        ["Fsms"]=new JArray(cachedPrefab.GetComponentsInChildren<PlayMakerFSM>(true)
                            .Select(f=>new JObject{["Name"]=f.FsmName,["Path"]=Hierarchy(f.transform),
                                ["States"]=new JArray((f.FsmStates??Array.Empty<FsmState>()).Select(st=>new JObject{
                                    ["Name"]=st.Name,["Transitions"]=new JArray(st.Transitions.Select(t=>new JObject{["Event"]=t.EventName,["To"]=t.ToState})),
                                    ["Actions"]=new JArray((st.Actions??Array.Empty<FsmStateAction>()).Select(a=>a.GetType().Name))}))}))
                    }.ToString(Formatting.None);
                    result.Success=true;bridge.Send(new(){Kind="result",Result=result});return;
                case "health-state":
                    RequireIsolation(); result.Success=true;
                    var healthRoot=ActorRoot((int)command["ActorId"]!);
                    result.Message=new JArray(healthRoot.GetComponentsInChildren<HealthManager>(true).Select(h=>new JObject{
                        ["Id"]=h.GetInstanceID(),["Path"]=Hierarchy(h.transform),["Hp"]=h.hp,["Dead"]=h.isDead,
                        ["SpecialDeath"]=h.hasSpecialDeath,["Invincible"]=h.IsInvincible,
                        ["Flags"]=JObject.FromObject(typeof(HealthManager).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                            .Where(f=>f.FieldType==typeof(bool)&&(f.Name.StartsWith("immune",StringComparison.OrdinalIgnoreCase)||f.Name.IndexOf("invinc",StringComparison.OrdinalIgnoreCase)>=0))
                            .ToDictionary(f=>f.Name,f=>f.GetValue(h)))
                    })).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "outline-state":
                    RequireIsolation();result.Success=true;
                    result.Message=new JArray(UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).Select(unit=>new JObject
                    {
                        ["Name"]=unit.name,["Friendly"]=unit.GetComponent<OwnedCompanion>()!=null,
                        ["Camera"]=Camera.main==null?null:new JObject{["Mode"]=Camera.main.transparencySortMode.ToString(),["Axis"]=JToken.FromObject(Camera.main.transparencySortAxis),["Position"]=JToken.FromObject(Camera.main.transform.position),["Forward"]=JToken.FromObject(Camera.main.transform.forward)},
                        ["Bodies"]=new JArray(unit.GetComponentsInChildren<Renderer>(true).Where(r=>r.GetComponent<SummonOutlineVisual>()==null&&r is not ParticleSystemRenderer&&r is not TrailRenderer).Select(r=>new JObject
                        { ["Name"]=r.name,["Path"]=Hierarchy(r.transform),["Active"]=r.gameObject.activeInHierarchy,["Width"]=r.bounds.size.x,["Height"]=r.bounds.size.y,
                          ["HasSprite"]=r.GetComponent<tk2dBaseSprite>()!=null||r is SpriteRenderer,
                          ["Position"]=JToken.FromObject(r.transform.position),["Center"]=JToken.FromObject(r.bounds.center),
                          ["SortingGroups"]=new JArray(r.GetComponentsInParent<UnityEngine.Rendering.SortingGroup>(true).Select(g=>new JObject{["Name"]=g.name,["Order"]=g.sortingOrder,["Layer"]=g.sortingLayerID})),
                          ["Materials"]=new JArray(r.sharedMaterials.Select(m=>new JObject{["Name"]=m!=null?m.name:"",["Shader"]=m?.shader?.name,["HasMainTex"]=m!=null&&m.HasProperty("_MainTex"),["Texture"]=m!=null&&m.HasProperty("_MainTex")?m.GetTexture("_MainTex")?.name:null})),
                          ["Enabled"]=r.enabled,["Visible"]=r.isVisible,["Queue"]=r.sharedMaterial?.renderQueue,["Order"]=r.sortingOrder,["Layer"]=r.sortingLayerID,["MaterialId"]=r.sharedMaterial!=null?r.sharedMaterial.GetInstanceID():0,["Shader"]=r.sharedMaterial?.shader?.name,["Color"]=r.sharedMaterial!=null&&r.sharedMaterial.HasProperty("_Color")?ColorUtility.ToHtmlStringRGBA(r.sharedMaterial.GetColor("_Color")):"" })),
                        ["Contours"]=new JArray(unit.GetComponentsInChildren<SummonOutlineVisual>(true).Select(marker=>new JObject
                        { ["Position"]=JToken.FromObject(marker.transform.position),["Center"]=JToken.FromObject(marker.GetComponent<Renderer>().bounds.center),
                          ["Enabled"]=marker.GetComponent<Renderer>()?.enabled??false,["Queue"]=marker.GetComponent<Renderer>()?.sharedMaterial?.renderQueue,["Order"]=marker.GetComponent<Renderer>()?.sortingOrder,["Layer"]=marker.GetComponent<Renderer>()?.sortingLayerID,["Colliders"]=marker.GetComponentsInChildren<Collider2D>(true).Length,["Vertices"]=marker.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount??0,
                          ["Colors"]=new JArray(marker.GetComponent<Renderer>().sharedMaterials.Select(material=>material!=null&&material.HasProperty("_FlashColor")?ColorUtility.ToHtmlStringRGBA(material.GetColor("_FlashColor")):"")) }))
                    })).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "friendly-state": result.Success=true;result.Message=FriendlySnapshot().ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "friendly-hit-measured": result.Success=true;result.Message=(await FriendlyHitMeasured(command)).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "friendly-guard-probe": result.Success=true;result.Message=FriendlyGuardProbe(command).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "native-freeze-positive": RequireIsolation();GameManager.instance.FreezeMoment((int?)command["FreezeType"]??10);break;
                case "incoming-contacts": result.Success=true;result.Message=IncomingContactProbe(command).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "ending-write-probe": result.Success=true;result.Message=EndingWriteProbe(command).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "prepare-contact-enemy": result.Success=true;result.Message=(await PrepareContactEnemy()).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "hostile-contact-fixture": ConfigureHostileContact(command);break;
                case "fixture-weapon": SetFixtureWeapon(command);break;
                case "terrain-fixture": result.Success=true;result.Message=ConfigureTerrain(command).ToString(Formatting.None);bridge.Send(new(){Kind="result",Result=result});return;
                case "facing-profile": RequireIsolation(); result.Success = true; result.Message = FacingProfiles().ToString(Formatting.None); bridge.Send(new() { Kind = "result", Result = result }); return;
                case "state": break;
                default: throw new InvalidOperationException("未知 QA 操作：" + operation);
            }
            result.Success = true; result.Message = Snapshot().ToString(Formatting.None);
        }
        catch (Exception exception) { result.Success = false; result.ErrorCode="QA_EXCEPTION"; result.Message = exception.ToString(); }
        bridge.Send(new BridgeMessage { Kind = "result", Result = result });
    }

    /// <summary>将存档目录、恢复点和全局写入隔离，阻断成就与社交统计上传。</summary>
    private void Isolate()
    {
        var config = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(QaHarness).Assembly.Location)!, "liveinteract-qa.json")));
        isolatedRoot = Path.GetFullPath((string)config["Root"]!);
        if (!isolatedRoot.Contains("audit-0.3")) { throw new InvalidOperationException("QA 根目录不符合本次专用范围。"); }
        Directory.CreateDirectory(isolatedRoot);
        var platform = Platform.Current as DesktopPlatform ?? throw new InvalidOperationException("当前平台不是 DesktopPlatform。");
        AccessTools.Field(typeof(DesktopPlatform), "saveDirPath").SetValue(platform, isolatedRoot);
        AccessTools.Field(typeof(DesktopPlatform), "desktopSaveRestoreHandler").SetValue(platform, new DesktopSaveRestoreHandler(isolatedRoot));
        if (!isolated)
        {
            foreach(var pair in new[]{(typeof(HealthManager),"HealToMax"),(typeof(HealthManager),"RefillHP"),
                (typeof(HealthManager),"AddHP"),(typeof(LifebloodState),"Start"),(typeof(LifebloodState),"Update"),
                (typeof(BlackThreadState),"SetupThreaded"),(typeof(HutongGames.PlayMaker.Actions.SetHP),"OnEnter"),
                (typeof(HutongGames.PlayMaker.Actions.AddHP),"OnEnter")})
                protection.Patch(AccessTools.Method(pair.Item1,pair.Item2),
                    prefix:new HarmonyMethod(typeof(QaHarness),nameof(BeforeNativeHpWrite)),
                    postfix:new HarmonyMethod(typeof(QaHarness),nameof(AfterNativeHpWrite)));
            protection.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.ActivateGameObject),"OnEnter"),
                prefix:new HarmonyMethod(typeof(QaHarness),nameof(TraceActivationFsm)));
            protection.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.DestroyObject),"OnEnter"),
                prefix:new HarmonyMethod(typeof(QaHarness),nameof(TraceDestroyFsm)));
            foreach(var method in typeof(GameManager).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                .Where(m=>m.Name is "FreezeMoment" or "SetTimeScale"))
                protection.Patch(method,prefix:new HarmonyMethod(typeof(QaHarness),nameof(TraceTimeControl)){priority=Priority.First});
            protection.Patch(AccessTools.Method(typeof(TimeManager),"CreateTimeControl"),
                prefix:new HarmonyMethod(typeof(QaHarness),nameof(TraceTimeControl)){priority=Priority.First});
            // 只在隔离测试内安装记录；默认关闭，不改变原作动作、对象或伤害。
            foreach (var actionType in new[] { typeof(HutongGames.PlayMaker.Actions.FaceObjectV2),
                typeof(HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool) })
            {
                protection.Patch(AccessTools.Method(actionType, "OnEnter"),
                    postfix: new HarmonyMethod(typeof(QaHarness), nameof(TraceNativeAction)));
            }
            foreach (var methodName in new[] { "DoTriggerEnter2D", "DoTriggerStay2D" })
            {
                protection.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Trigger2dEventLayer), methodName),
                    prefix: new HarmonyMethod(typeof(QaHarness), nameof(TraceNativeTrigger)));
            }
            var suppress = new HarmonyMethod(typeof(QaHarness), nameof(Suppress));
            foreach (var name in new[] { "PushAchievementUnlock", "UpdateAchievementProgress", "ResetAchievements" })
            {
                protection.Patch(AccessTools.Method(typeof(DesktopPlatform), name), prefix: suppress);
            }
            foreach(var death in typeof(HealthManager).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.Name=="Die"&&m.GetMethodBody()!=null))protection.Patch(death,prefix:new HarmonyMethod(typeof(QaHarness),nameof(TraceForcedDeath)));
            protection.Patch(AccessTools.Method(typeof(HealthManager),"Hit",new[]{typeof(HitInstance)}),
                prefix:new HarmonyMethod(typeof(QaHarness),nameof(BeforeSummonHit)),postfix:new HarmonyMethod(typeof(QaHarness),nameof(AfterSummonHit)));
            protection.Patch(AccessTools.Method(typeof(HeroController),nameof(HeroController.TakeDamage)), prefix:new HarmonyMethod(typeof(QaHarness),nameof(BeforeHeroDamage)),postfix:new HarmonyMethod(typeof(QaHarness),nameof(AfterHeroDamage)));
            protection.Patch(AccessTools.Method(typeof(PlayerData),nameof(PlayerData.TakeHealth),new[]{typeof(int),typeof(bool),typeof(bool)}),prefix:new HarmonyMethod(typeof(QaHarness),nameof(BeforeHealthLoss)),postfix:new HarmonyMethod(typeof(QaHarness),nameof(AfterHeroDamage)));
            protection.Patch(AccessTools.Method(typeof(Platform), "AddSocialStat"), prefix: suppress);
            protection.Patch(AccessTools.Method(typeof(JsonSharedData), "Save"), prefix: suppress);
            protection.Patch(AccessTools.Method(typeof(PlayerPrefsSharedData), "Save"), prefix: suppress);
        }
        isolated = true;
    }

    /// <summary>要求实际隔离字段仍指向本次目录，防止加载到原存档。</summary>
    private string RequireIsolation()
    {
        if (!isolated || (string)AccessTools.Field(typeof(DesktopPlatform), "saveDirPath").GetValue(Platform.Current) != isolatedRoot)
        {
            throw new InvalidOperationException("QA 存档尚未隔离，拒绝变更游戏状态。");
        }
        return isolatedRoot;
    }

    /// <summary>建立可比较的角色基线，不影响原用户档位。</summary>
    private void Prepare(bool unlock)
    {
        actions.Reset(); Time.timeScale=1; var hero = HeroController.SilentInstance; var data = PlayerData.instance;
        data.maxHealthBase = 9; data.maxHealth = 9; data.health = 4; data.healthBlue = 0;
        data.IsSilkSpoolBroken = false; data.silkMax = 18; data.silk = 6; data.geo = 100; data.disableSilkAbilities = false;
        if (unlock)
        {
            data.hasDash = true; data.hasWalljump = true; data.hasDoubleJump = true;
            data.hasSilkSpecial = true; data.hasNeedleThrow = true; data.hasThreadSphere = true; data.hasParry = true;
            data.hasSilkCharge = true; data.hasSilkBomb = true; data.hasSilkBossNeedle = true;
        }
        hero.cState.dead = false; hero.cState.transitioning = false; hero.silkSpecialFSM?.SendEvent("COOLDOWN CANCEL"); hero.ResetState(); hero.RegainControl(); hero.AcceptInput();
        if (!anchor.HasValue) { anchor = hero.transform.position; }
    }

    /// <summary>读取具体前后数值，不能仅凭成功回执判断效果。</summary>
    private JObject Snapshot()
    {
        var manager = AccessTools.Field(typeof(GameManager), "_instance").GetValue(null) as GameManager;
        var hero = AccessTools.Field(typeof(HeroController), "_instance").GetValue(null) as HeroController;
        var data = AccessTools.Field(typeof(PlayerData), "_instance").GetValue(null) as PlayerData;
        var snapshot = new JObject { ["Scene"] = SceneManager.GetActiveScene().name, ["TimeScale"] = Time.timeScale, ["Isolated"] = isolated, ["CanAct"] = actions.CanAct, ["Effects"] = JArray.FromObject(actions.CaptureStatus().ActiveEffects) };
        snapshot["Pickups"] = new JArray(actions.Pickups.Select(p => new JObject { ["Kind"] = p.Kind, ["Amount"] = p.Amount, ["Who"] = p.Who, ["X"] = p.Position.x, ["Y"] = p.Position.y }));
        snapshot["AppearanceLayers"] = new JArray(UnityEngine.Object.FindObjectsByType<AppearanceStack>(FindObjectsSortMode.None).Where(a => !a.Retired).Select(a => new JObject { ["Target"] = a.name, ["Count"] = a.Count, ["Multiplier"] = a.Multiplier, ["RootScale"] = a.transform.localScale.x, ["ColliderWidth"] = a.GetComponentInChildren<Collider2D>()?.bounds.size.x, ["Labels"] = new JArray(a.Labels()) }));
        if (data != null)
        {
            var values = new JObject();
            foreach (var field in new[] { "health", "maxHealth", "maxHealthBase", "healthBlue", "silk", "silkMax", "geo", "nailUpgrades", "hasDash", "hasNeedleThrow", "hasThreadSphere", "CurrentCrestID" })
            {
                values[field] = JToken.FromObject(AccessTools.Field(typeof(PlayerData), field).GetValue(data));
            }
            values["RespawnScene"] = data.respawnScene; values["RespawnMarker"] = data.respawnMarkerName;
            values["CurrentSilkMax"] = data.CurrentSilkMax; values["IsSilkSpoolBroken"] = data.IsSilkSpoolBroken; values["ShellShards"] = data.ShellShards; values["SilkSkillCost"] = data.SilkSkillCost; snapshot["Player"] = values;
        }
        if (hero != null)
        {
            var values = new JObject { ["X"] = hero.transform.position.x, ["Y"] = hero.transform.position.y, ["ScaleX"] = hero.transform.localScale.x, ["ScaleY"] = hero.transform.localScale.y, ["Dead"] = hero.cState.dead, ["Transitioning"] = hero.cState.transitioning, ["CanInput"] = hero.CanInput(), ["CanTakeDamage"] = hero.CanTakeDamage() };
            foreach (var field in new[] { "RUN_SPEED", "WALK_SPEED", "JUMP_SPEED", "DASH_SPEED", "DEFAULT_GRAVITY" }) { values[field] = JToken.FromObject(AccessTools.Field(typeof(HeroController), field).GetValue(hero)); }
            values["VelocityX"] = hero.GetComponent<Rigidbody2D>().linearVelocity.x; values["VelocityY"] = hero.GetComponent<Rigidbody2D>().linearVelocity.y;
            values["HeroVisible"] = hero.GetComponentsInChildren<Renderer>().Any(r => r.enabled);
            values["SkillState"] = hero.silkSpecialFSM?.ActiveStateName;
            values["AirDashed"] = (bool)AccessTools.Field(typeof(HeroController), "airDashed").GetValue(hero);
            values["DoubleJumped"] = (bool)AccessTools.Field(typeof(HeroController), "doubleJumped").GetValue(hero);
            values["CombatState"] = hero.spellControl?.ActiveStateName;
            snapshot["Hero"] = values;
        }
        if (manager != null && manager.IsGameplayScene())
        {
            snapshot["Enemies"] = new JArray(UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None).Where(e => e.hp > 0 && !e.isDead).Take(80).Select(e => new JObject { ["Id"] = e.GetInstanceID(), ["Name"] = e.name, ["Hp"] = e.hp, ["X"] = e.transform.position.x, ["Y"] = e.transform.position.y, ["ScaleX"] = e.transform.localScale.x, ["ScaleY"] = e.transform.localScale.y, ["Animator"] = e.GetComponentInChildren<Animator>()?.speed, ["Tk2d"] = e.GetComponentInChildren<tk2dSpriteAnimator>()?.CurrentClip?.name, ["AnimationRate"] = e.GetComponentInChildren<tk2dSpriteAnimator>() is { } animation ? actions.AnimationSpeed(animation) : 1 }));
        }
        if (manager != null && manager.IsGameplayScene())
        {
            snapshot["ToolCapacities"] = JObject.FromObject(ToolItemManager.GetAllTools().ToDictionary(tool => tool.name, ToolItemManager.GetToolStorageAmount));
            snapshot["OwnedCompanions"] = new JArray(Resources.FindObjectsOfTypeAll<ClockworkHatchling>().Where(h => h != null && h.name.Contains("[LiveInteract]")).Select(h => new JObject
            {
                ["Id"] = h.GetInstanceID(), ["Name"] = h.name, ["Scene"] = h.gameObject.scene.name, ["VisibleMeshes"] = h.GetComponentsInChildren<MeshRenderer>().Count(r => r.enabled && !r.forceRenderingOff), ["Active"] = h.gameObject.activeInHierarchy, ["State"] = h.CurrentState.ToString(),
                ["X"] = h.transform.position.x, ["Y"] = h.transform.position.y, ["Damage"] = h.normalDetails.damage,
                ["LastTargetDamaged"] = h.lastTargetDamaged != null ? h.lastTargetDamaged.name : "",
                ["DamageHeroEnabled"] = h.GetComponentsInChildren<DamageHero>(true).Count(d => d.enabled),
                ["HasToolSource"] = (bool)AccessTools.Field(typeof(ClockworkHatchling), "hasToolSource").GetValue(h)
            }));
            snapshot["SummonLabels"] = new JArray(actions.SpawnLabels().Select(label => new JObject { ["Name"] = label.Name, ["Friendly"] = label.IsFriendly }));
            snapshot["UnlockedTools"] = ToolItemManager.GetUnlockedTools().Count();
            snapshot["UnlockedCrests"] = ToolItemManager.GetAllCrests().Count(crest => crest.IsUnlocked);
            snapshot["Tools"] = JObject.FromObject(ToolItemManager.GetUnlockedTools().ToDictionary(tool => tool.name, tool => (int)AccessTools.Field(AccessTools.Property(typeof(ToolItem), "SavedData").GetValue(tool, null).GetType(), "AmountLeft").GetValue(AccessTools.Property(typeof(ToolItem), "SavedData").GetValue(tool, null))));
            var modifiers = new JObject();
            foreach (var key in new[] { "allDamage", "needleDamage", "toolDamage", "silkDamage", "incomingDamage", "knockback", "attackSpeed", "invincible", "survive", "infiniteSilk", "infiniteTools", "shieldPoints" }) { modifiers[key] = JToken.FromObject(AccessTools.Field(typeof(GameActions), key).GetValue(actions)); }
            snapshot["Modifiers"] = modifiers;
            var cameras = AccessTools.Field(typeof(GameCameras), "_instance").GetValue(null) as GameCameras;
            if (cameras != null)
            {
                snapshot["HudVisible"] = cameras.hudCamera != null && cameras.hudCamera.enabled;
                snapshot["CameraZoom"] = cameras.tk2dCam != null ? JToken.FromObject(cameras.tk2dCam.GetType().GetProperty("ZoomFactor").GetValue(cameras.tk2dCam, null)) : null;
                if (cameras.brightnessEffect != null) { snapshot["Brightness"] = cameras.brightnessEffect._Brightness; snapshot["Contrast"] = cameras.brightnessEffect._Contrast; }
            }
        }
        snapshot["NativeRainCoins"]=Resources.FindObjectsOfTypeAll<OwnedRainPickup>().Count(p=>p!=null&&p.StillOwned&&p.gameObject.scene.IsValid()&&p.gameObject.activeInHierarchy);
        snapshot["PrefabCacheCount"]=actions.PrefabCacheCount;
        snapshot["PrivateMemoryMB"]=System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64/1048576;
        snapshot["DamageLog"]=new JArray(damageLog.Select(d=>d.DeepClone()));
        snapshot["ActorEvents"]=new JArray(actorEvents.Select(d=>d.DeepClone()));
        snapshot["NativeActors"]=new JArray(Resources.FindObjectsOfTypeAll<OwnedSummon>().Where(actor=>actor.gameObject.scene.IsValid()).Select(actor=>new JObject {
            ["Id"]=actor.GetInstanceID(),["AssetKey"]=actor.AssetKey,["Name"]=actor.name,["Active"]=actor.gameObject.activeInHierarchy,["Scene"]=actor.gameObject.scene.name,["X"]=actor.transform.position.x,["Y"]=actor.transform.position.y,
            ["RootPositionConstraints"]=new JArray(actor.GetComponents<ConstrainPosition>().Select(c=>new JObject{
                ["Enabled"]=c.enabled,["LocalSpace"]=c.localSpace,["XEnabled"]=c.constrainX,["MinX"]=c.xMin,["MaxX"]=c.xMax,
                ["YEnabled"]=c.constrainY,["MinY"]=c.yMin,["MaxY"]=c.yMax})),
            ["Components"]=new JArray(actor.GetComponents<MonoBehaviour>().Where(c=>c!=null).Select(c=>c.GetType().Name)),["Boot"]=actor.CombatBootStatus,["Friendly"]=actor.GetComponent<OwnedCompanion>()!=null,["Hp"]=actor.GetComponentInChildren<HealthManager>()?.hp,["Dead"]=actor.GetComponentInChildren<HealthManager>()?.isDead,
            ["Target"]=actor.GetComponent<NativeEnemyAlly>()?.TargetObject?.name,["HasEnemy"]=actor.GetComponent<NativeEnemyAlly>()?.HasEnemy,
            ["NativeHeroReferences"]=new JArray(actor.GetComponentsInChildren<MonoBehaviour>(true).Where(c=>c!=null&&c.GetType().Assembly==typeof(HealthManager).Assembly).SelectMany(c=>c.GetType().GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Where(f=>f.FieldType==typeof(GameObject)||f.FieldType==typeof(Transform)).Select(f=>new {Component=c,Field=f})).Select(x=>new{Field=x.Field.Name,Value=x.Field.GetValue(x.Component) as UnityEngine.Object}).Where(x=>x.Value!=null&&(x.Field.ToLowerInvariant().Contains("hero")||x.Field.ToLowerInvariant().Contains("target"))).Select(x=>new JObject{["Field"]=x.Field,["Value"]=x.Value!.name})),
            ["HealthFlags"]=actor.GetComponentInChildren<HealthManager>()==null?"":JsonConvert.SerializeObject(new {actor.GetComponentInChildren<HealthManager>().IsInvincible}),
            ["Colliders"]=new JArray(actor.GetComponentsInChildren<Collider2D>(true).Select(c=>new JObject{["Name"]=c.name,["Enabled"]=c.enabled,["Trigger"]=c.isTrigger,["Layer"]=c.gameObject.layer,["CenterX"]=c.bounds.center.x,["CenterY"]=c.bounds.center.y,["SizeX"]=c.bounds.size.x,["SizeY"]=c.bounds.size.y})),
            ["DamageComponents"]=new JArray(actor.GetComponentsInChildren<DamageHero>(true).Select(w=>new JObject{["Name"]=w.name,["Enabled"]=w.enabled,["Active"]=w.gameObject.activeInHierarchy,["Damage"]=w.damageDealt})),
            ["Fsms"]=new JArray(actor.GetComponentsInChildren<PlayMakerFSM>(true).Select(f=>new JObject{["Name"]=f.FsmName,["State"]=f.ActiveStateName,["Enabled"]=f.enabled,["ObjectActive"]=f.gameObject.activeInHierarchy,["Targets"]=new JArray(f.FsmVariables.GameObjectVariables.Where(v=>v.Value!=null&&(v.Name.ToLowerInvariant().Contains("hero")||v.Name.ToLowerInvariant().Contains("target")||v.Value.GetComponent<HeroController>()!=null)).Select(v=>new JObject{["Key"]=v.Name,["Object"]=v.Value.name,["Hero"]=v.Value.GetComponent<HeroController>()!=null}))}))
        }));
        snapshot["Projectiles"]=new JArray(UnityEngine.Object.FindObjectsByType<DamageHero>(FindObjectsSortMode.None).Where(w=>w.GetComponentInParent<OwnedSummon>()==null).Take(80).Select(w=>
        {
            var projectileOwner=w.GetComponent<NativeAllyProjectile>()?.Owner;
            return new JObject{["Name"]=w.name,["Enabled"]=w.enabled,["Friendly"]=NativeAllyExecution.IsFriendly(w.gameObject),["Owner"]=projectileOwner!=null?projectileOwner.name:null,["X"]=w.transform.position.x,["Y"]=w.transform.position.y};
        }));
        return snapshot;
    }

    /// <summary>只展开可读的原生动作参数，不序列化 Unity 对象图。</summary>
    private static JToken DescribeFsmValue(object? value)
    {
        if(value==null)return JValue.CreateNull();
        // 只展开动作参数数组和二维/三维值，便于核对原作射线、条件和坐标；不遍历对象图。
        if(value is Array array)return new JArray(array.Cast<object?>().Select(DescribeFsmValue));
        if(value is FsmVector2 vector2)return new JObject{["X"]=vector2.Value.x,["Y"]=vector2.Value.y};
        if(value is FsmVector3 vector3)return new JObject{["X"]=vector3.Value.x,["Y"]=vector3.Value.y,["Z"]=vector3.Value.z};
        if(value is FsmFloat number)return new JValue(number.Value);
        if(value is FsmInt integer)return new JValue(integer.Value);
        if(value is FsmBool boolean)return new JValue(boolean.Value);
        if(value is FsmString text)return new JValue(text.Value);
        if(value is FsmGameObject obj)return new JValue(obj.Value!=null?obj.Value.name:"null");
        if(value is FsmObject reference)
        {
            var nativeValue=reference.Value;
            return new JObject{["VariableName"]=reference.Name,["ValueType"]=nativeValue!=null?nativeValue.GetType().Name:null,
                ["Name"]=nativeValue!=null?nativeValue.name:null,["Id"]=nativeValue!=null?(int?)nativeValue.GetInstanceID():null};
        }
        if(value is FsmProperty property)
        {
            var target=property.TargetObject?.Value;
            return new JObject{["PropertyName"]=property.PropertyName,["TargetType"]=property.TargetTypeName,
                ["TargetName"]=target!=null?target.name:null,["TargetId"]=target!=null?(int?)target.GetInstanceID():null,
                ["BoolVariable"]=property.BoolParameter?.Name,["BoolValue"]=property.BoolParameter?.Value};
        }
        if(value is FsmOwnerDefault owner)return new JObject{["Owner"]=owner.OwnerOption.ToString(),
            ["Target"]=owner.GameObject?.Value!=null?owner.GameObject.Value.name:"null"};
        if(value is FunctionCall call)return new JObject{["Function"]=call.FunctionName,["ParameterType"]=call.ParameterType,
            ["String"]=call.StringParameter?.Value,["Int"]=call.IntParameter?.Value};
        if(value is FsmEventTarget eventTarget)return new JObject{["TargetType"]=eventTarget.target.ToString(),
            ["Object"]=DescribeFsmValue(eventTarget.gameObject),["FsmName"]=eventTarget.fsmName?.Value,
            ["FsmPath"]=eventTarget.fsmComponent!=null?Hierarchy(eventTarget.fsmComponent.transform):null};
        if(value is FsmEvent ev)return new JValue(ev.Name);
        if(value is UnityEngine.Object native)return new JValue(native!=null?native.name:"null");
        if(value is string||value is bool||value.GetType().IsPrimitive||value.GetType().IsEnum)return JToken.FromObject(value);
        return new JValue(value.GetType().Name);
    }

    private static void TraceActivationFsm(HutongGames.PlayMaker.Actions.ActivateGameObject __instance)
    {
        var actor=__instance.Owner?.GetComponentInParent<OwnedSummon>();if(actor==null)return;
        var target=__instance.Fsm.GetOwnerDefaultTarget(__instance.gameObject);
        actorEvents.Add(new JObject{["Kind"]="fsm-activate",["Name"]=actor.name,
            ["Fsm"]=__instance.Fsm.Name,["State"]=__instance.State.Name,["Activate"]=__instance.activate.Value,
            ["Target"]=target!=null?Hierarchy(target.transform):"null"});
        if(actorEvents.Count>200)actorEvents.RemoveAt(0);
    }

    private static void TraceDestroyFsm(HutongGames.PlayMaker.Actions.DestroyObject __instance)
    {
        var actor=__instance.Owner?.GetComponentInParent<OwnedSummon>();
        if(actor!=null)RecordActor(actor.gameObject,"fsm-destroy:"+__instance.Fsm.Name+"/"+__instance.State.Name);
    }
    private sealed class NativeHpWrite
    {
        public HealthManager Health=null!;public int Before;public string Method="";public string Stack="";
    }
    private static void BeforeNativeHpWrite(object __instance,MethodBase __originalMethod,out NativeHpWrite? __state)
    {
        __state=null;
        var obj=(__instance as Component)?.gameObject??(__instance as FsmStateAction)?.Owner;
        var actor=obj!=null?obj.GetComponentInParent<OwnedSummon>():null;
        if(actor==null)return;
        var health=actor.GetComponentInChildren<HealthManager>();if(health==null)return;
        __state=new NativeHpWrite{Health=health,Before=health.hp,Method=__originalMethod.DeclaringType!.Name+"."+__originalMethod.Name,Stack=Environment.StackTrace};
    }
    private static void AfterNativeHpWrite(NativeHpWrite? __state)
    {
        if(__state==null||__state.Health==null||__state.Before==__state.Health.hp)return;
        actorEvents.Add(new JObject{["Kind"]="native-hp-writer",["Method"]=__state.Method,
            ["Before"]=__state.Before,["After"]=__state.Health.hp,["Stack"]=__state.Stack});
        if(actorEvents.Count>200)actorEvents.RemoveAt(0);
    }

    /// <summary>测试档禁止真实成就或共享偏好写回。</summary>
    private static void TraceForcedDeath(HealthManager __instance)
    {
        if(__instance.hp>0&&__instance.GetComponentInParent<OwnedSummon>()!=null)
            actorEvents.Add(new JObject{["Kind"]="forced-death",["Name"]=__instance.name,["Hp"]=__instance.hp,["Stack"]=Environment.StackTrace});
    }
    private static void BeforeSummonHit(HealthManager __instance,HitInstance __0,out JObject? __state)
    {
        __state=null;
        var targetOwner=__instance.GetComponentInParent<OwnedSummon>();
        if(targetOwner==null&&__instance.GetComponentInParent<OwnedCompanion>()==null)return;
        __state=new JObject{["Kind"]="summon-hit",["Name"]=__instance.name,["Before"]=__instance.hp,
            ["TargetId"]=targetOwner!=null?targetOwner.GetInstanceID():__instance.GetInstanceID(),["TargetHealthId"]=__instance.GetInstanceID(),["TargetAsset"]=targetOwner?.AssetKey,
            ["SourceFriendly"]=NativeAllyExecution.IsFriendly(__0.Source),
            ["Source"]=__0.Source!=null?Hierarchy(__0.Source.transform):"null",["Damage"]=__0.DamageDealt,
            ["HeroDamage"]=__0.IsHeroDamage,["X"]=__instance.transform.position.x,["Y"]=__instance.transform.position.y,
            ["SourceX"]=__0.Source!=null?__0.Source.transform.position.x:0,["SourceY"]=__0.Source!=null?__0.Source.transform.position.y:0,
            ["Distance"]=__0.Source!=null?Vector2.Distance(__instance.transform.position,__0.Source.transform.position):-1,
            ["SourceContacts"]=ReadHitSourceContacts(__0.Source,__instance),["Stack"]=Environment.StackTrace};
    }
    /// <summary>只读记录命中瞬间原作来源的同物体碰撞，不扩大攻击范围或发送命中。</summary>
    /// <param name="source">实际HitInstance携带的伤害来源。</param>
    /// <param name="target">正在接受本次命中的生命组件。</param>
    /// <returns>活动来源碰撞数及接触目标身体的数量，供定位隔空伤害。</returns>
    private static JObject ReadHitSourceContacts(GameObject? source,HealthManager target)
    {
        // 不递归读取子级警戒框；只观察与本次来源相同物体的碰撞。
        var active=0;var bodyContacts=0;var overlaps=new List<Collider2D>();var touchingBody=new List<Collider2D>();
        if(source!=null&&source.activeInHierarchy)
        foreach(var box in source.GetComponents<Collider2D>())
        {
            if(!box.enabled)continue;active++;
            var filter=new ContactFilter2D{useTriggers=true,useLayerMask=false};
            overlaps.Clear();box.Overlap(filter,overlaps);touchingBody.Clear();box.GetContacts(filter,touchingBody);
            foreach(var contact in touchingBody)if(!overlaps.Contains(contact))overlaps.Add(contact);
            foreach(var contact in overlaps)
            {
                if(contact==null||!contact.enabled||!contact.gameObject.activeInHierarchy||contact.GetComponentInParent<HealthManager>()!=target)continue;
                if(contact.isTrigger&&contact.gameObject.layer!=11&&contact.gameObject.layer!=target.gameObject.layer&&contact.GetComponent<HealthManager>()==null)continue;
                bodyContacts++;
            }
        }
        return new JObject{["ActiveColliders"]=active,["TargetBodyOverlaps"]=bodyContacts};
    }

    private static void AfterSummonHit(HealthManager __instance,JObject? __state)
    {
        if(__state==null)return;__state["After"]=__instance!=null?__instance.hp:0;
        actorEvents.Add(__state);if(actorEvents.Count>200)actorEvents.RemoveAt(0);
    }

    private static void TraceTimeControl(MethodBase __originalMethod,object[] __args)
    {
        actorEvents.Add(new JObject{["Kind"]="time-control",["Method"]=__originalMethod.ToString(),
            ["Arguments"]=new JArray(__args.Select(a=>a?.ToString()??"null")),
            ["Spawned"]=NativeAllyExecution.InSpawnedFlow,["Friendly"]=NativeAllyExecution.IsFriendlyFlow,
            ["Origin"]=NativeAllyExecution.Origin!=null?NativeAllyExecution.Origin.name:"null",
            ["Resource"]=NativeAllyExecution.ResourceKey,["Stack"]=Environment.StackTrace});
        if(actorEvents.Count>200)actorEvents.RemoveAt(0);
    }

    /// <summary>只读记录友军原作触发动作实际接到的碰撞物，不发送事件。</summary>
    /// <param name="__instance">原作当前触发动作。</param>
    /// <param name="__0">原生物理回调传入的碰撞体。</param>
    private static void TraceNativeTrigger(HutongGames.PlayMaker.Actions.Trigger2dEventLayer __instance, Collider2D __0)
    {
        if (!nativeActionTraceEnabled || __0 == null) { return; }
        var owned = __instance.Owner?.GetComponentInParent<OwnedSummon>();
        if (owned == null || owned.GetComponent<OwnedCompanion>() == null) { return; }
        if (!__instance.collideLayer.IsNone && __instance.collideLayer.Value != __0.gameObject.layer) { return; }

        // 与实际出手日志分开标识，只观察碰撞来源及事件，不改变阵营过滤。
        nativeActionTrace.Add(new JObject { ["Kind"] = "native-trigger", ["Frame"] = Time.frameCount,
            ["Time"] = Time.realtimeSinceStartup, ["ActorId"] = owned.GetInstanceID(),
            ["Asset"] = owned.AssetKey, ["Fsm"] = __instance.Fsm.Name, ["State"] = __instance.State.Name,
            ["OwnerPath"] = __instance.Owner != null ? Hierarchy(__instance.Owner.transform) : null, ["OtherPath"] = Hierarchy(__0.transform),
            ["OtherHero"] = __0.GetComponentInParent<HeroController>() != null,
            ["OtherFriendly"] = NativeAllyExecution.IsFriendly(__0.gameObject),
            ["Layer"] = __0.gameObject.layer, ["Event"] = __instance.sendEvent?.Name,
            ["OwnX"] = owned.transform.position.x, ["OtherX"] = __0.bounds.center.x });
        if (nativeActionTrace.Count > 2000) { nativeActionTrace.RemoveAt(0); }
    }

    /// <summary>记录原作动作实际读取的目标和弹体参数，不执行或重入动作。</summary>
    /// <param name="__instance">已由原作正常执行的动作。</param>
    private static void TraceNativeAction(FsmStateAction __instance)
    {
        if (!nativeActionTraceEnabled) { return; }
        var owned = __instance.Owner?.GetComponentInParent<OwnedSummon>();
        if (owned == null || owned.GetComponent<OwnedCompanion>() == null) { return; }
        var ally = owned.GetComponent<NativeEnemyAlly>();
        var expected = ally?.TargetObject;
        var row = new JObject
        {
            ["Frame"] = Time.frameCount, ["Time"] = Time.realtimeSinceStartup,
            ["ActorId"] = owned.GetInstanceID(), ["Asset"] = owned.AssetKey,
            ["Fsm"] = __instance.Fsm.Name, ["State"] = __instance.State.Name,
            ["Action"] = __instance.GetType().Name, ["X"] = owned.transform.position.x,
            ["Y"] = owned.transform.position.y, ["ScaleX"] = owned.transform.localScale.x,
            ["ExpectedId"] = expected?.GetComponent<OwnedSummon>()?.GetInstanceID(),
            ["ExecutionFriendly"] = NativeAllyExecution.IsFriendlyFlow,
            ["ExecutionOwnerId"] = NativeAllyExecution.Current?.GetComponent<OwnedSummon>()?.GetInstanceID()
        };
        if (__instance is HutongGames.PlayMaker.Actions.FaceObjectV2 face)
        {
            var target = face.objectB.Value;
            row["ReadTargetId"] = target?.GetComponent<OwnedSummon>()?.GetInstanceID();
            row["ReadTargetName"] = target != null ? target.name : "null";
            row["ReadTargetX"] = target != null ? target.transform.position.x : (float?)null;
            row["ReadTargetY"] = target != null ? target.transform.position.y : (float?)null;
            row["TargetIsHero"] = target != null && target.GetComponentInParent<HeroController>() != null;
            row["SpriteFacesRight"] = face.spriteFacesRight.Value;
            row["VariableName"] = face.objectB.Name;
        }
        else
        {
            row["Fields"] = new JObject(__instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => new JProperty(field.Name, DescribeFsmValue(field.GetValue(__instance)))));
        }
        nativeActionTrace.Add(row);
        if (nativeActionTrace.Count > 2000) { nativeActionTrace.RemoveAt(0); }
    }

    private static void BeforeHeroDamage(GameObject __0,out JObject __state)
    {
        __state=new JObject{["At"]=Time.realtimeSinceStartup,["Source"]=__0!=null?__0.name:"null",["Path"]=__0!=null?Hierarchy(__0.transform):"null",["Friendly"]=NativeAllyExecution.IsFriendly(__0),["Before"]=PlayerData.instance.health,["Context"]=NativeAllyExecution.Current!=null?NativeAllyExecution.Current.name:"null",["Stack"]=Environment.StackTrace};
    }
    private static void AfterHeroDamage(JObject __state)
    {
        if(__state==null)return;__state["After"]=PlayerData.instance.health;damageLog.Add(__state);if(damageLog.Count>120)damageLog.RemoveAt(0);
    }
    private static void BeforeHealthLoss(int __0,out JObject __state)
    {
        __state=new JObject{["At"]=Time.realtimeSinceStartup,["Source"]="PlayerData.TakeHealth",["Amount"]=__0,["Before"]=PlayerData.instance.health,["Context"]=NativeAllyExecution.Current!=null?NativeAllyExecution.Current.name:"null",["Stack"]=Environment.StackTrace};
    }
    private static string Hierarchy(Transform t)=>t.parent!=null?Hierarchy(t.parent)+"/"+t.name:t.name;

    private static bool Suppress() => false;

    /// <summary>正常退出自己启动的测试游戏，等待回执发送后由 Unity 关闭。</summary>
    private IEnumerator QuitAfterReply()
    {
        yield return new WaitForSecondsRealtime(1);
        Application.Quit();
    }
}
/// <summary>隔离复现：发射者消失不能取消仍存活的独立弹体回收协程。</summary>
internal sealed class QaRoutineProbe : MonoBehaviour
{
    public int Steps;
    public bool StayedFriendly=true;
    public bool TargetedHero;
    public IEnumerator Run()
    {
        for(var index=0;index<3;index++)
        {
            Steps++;StayedFriendly&=NativeAllyExecution.IsFriendlyFlow;
            TargetedHero|=NativeAllyExecution.TargetGameObject(HeroController.SilentInstance)==HeroController.SilentInstance.gameObject;
            yield return new WaitForSecondsRealtime(.04f);
        }
    }
}
/// <summary>确定性的接触靶：只供隔离 QA，使用真实原作敌人和真实伤害碰撞，不注入伤害。</summary>
internal sealed class QaContactTarget : MonoBehaviour
{
    public Transform? Subject;
    private void LateUpdate()
    {
        if(Subject==null)Subject=UnityEngine.Object.FindObjectsByType<NativeEnemyAlly>(FindObjectsSortMode.None).FirstOrDefault()?.transform;
        if(Subject==null)return;
        foreach(var damager in GetComponentsInChildren<DamageHero>(true)){damager.damageDealt=0;damager.enabled=false;}
        // 不把未激活攻击（零面积碰撞）当成接触位置；原作远程弹体也可以作为真实接触靶。
        var attacks=Subject.GetComponentsInChildren<DamageHero>(true)
            .Concat(UnityEngine.Object.FindObjectsByType<NativeAllyProjectile>(FindObjectsSortMode.None)
                .Where(p=>p.Owner!=null&&p.Owner.transform==Subject)
                .SelectMany(p=>p.GetComponentsInChildren<DamageHero>(true)));
        var attack=attacks.Where(w=>w.enabled&&w.gameObject.activeInHierarchy&&w.damageDealt>0)
            .SelectMany(w=>w.GetComponents<Collider2D>())
            .Concat(Subject.GetComponentsInChildren<DamageEnemies>(true)
                .Where(w=>w.enabled&&w.gameObject.activeInHierarchy)
                .SelectMany(w=>w.GetComponents<Collider2D>()))
            .FirstOrDefault(c=>c.enabled&&c.gameObject.activeInHierarchy&&c.bounds.size.sqrMagnitude>.0001f);
        var own=GetComponent<Collider2D>();
        var center=attack!=null?attack.bounds.center:Subject.position;
        transform.position=center-(own!=null?(Vector3)own.offset:Vector3.zero);Physics2D.SyncTransforms();
    }
}
#endif


