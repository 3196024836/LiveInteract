using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using LiveInteract.Contracts;
using Object = UnityEngine.Object;

namespace LiveInteract.Silksong;

/// <summary>只在游戏主线程执行经过目录校验的动作，集中维护临时状态和本工具创建的对象。</summary>
internal sealed class GameActions
{
    // 与游戏解耦的恢复容器与仅本工具持有的资源。
    private static readonly FieldInfo HeroInstanceField = AccessTools.Field(typeof(HeroController), "_instance");
    private static readonly FieldInfo ManagerInstanceField = AccessTools.Field(typeof(GameManager), "_instance");
    private static readonly FieldInfo CamerasInstanceField = AccessTools.Field(typeof(GameCameras), "_instance");
    private readonly TimedEffects effects = new();
    private readonly GameAssetCatalog monsterAssets = new();
    private readonly List<GameObject> spawned = new();
    private readonly Dictionary<int, string> spawnedOwners = new();
    private readonly List<CompanionLease> companions = new();
    private readonly List<CompanionLease> enemyTimers = new();
    private GameObject? companionRoot;
    private readonly List<SupportPickup> pickups = new();
    private readonly List<AppearanceStack> appearanceStacks = new();
    private bool relocatePickups;
    public event Action<string, string>? PickupCollected;
    public IReadOnlyList<SupportPickup> Pickups => pickups;
    private readonly Dictionary<int, double> animationSpeeds = new();
    private readonly List<AsyncOperationHandle<GameObject>> assetHandles = new();
    private readonly Dictionary<string, AsyncOperationHandle<GameObject>> namedPrefabHandles = new();
    private readonly List<string> diagnostics = new();
    private readonly HashSet<string> exercised = new();
    private readonly List<string> sceneKeys = new();
    private readonly List<string> prefabKeys = new();
    private bool indexedResources;
    private Vector3? markedPosition;
    private string markedScene = "";
    private string lastScene = "";
    private GameObject? staging;
    private int generation;
    private int stopEpoch;
    private readonly List<GameObject> rainCoins = new();
    private float nextRefill;
    private float nextHostileScan;
    private HealthManager[] hostileCache = Array.Empty<HealthManager>();
    internal HealthManager? FindHostile(Vector3 position,float radius)=>hostileCache.Where(e=>e!=null && !e.isDead && e.hp>0 && e.gameObject.activeInHierarchy && Vector3.Distance(e.transform.position,position)<=radius).OrderBy(e=>Vector3.SqrMagnitude(e.transform.position-position)).FirstOrDefault();
    private float nextCapture;
    private bool infiniteSilk;
    private bool infiniteTools;
    private bool invincible;
    private int shieldPoints;
    private bool survive;
    private double allDamage = 1, needleDamage = 1, toolDamage = 1, silkDamage = 1, incomingDamage = 1, knockback = 1, attackSpeed = 1;

    /// <summary>当前游戏端实例，供确定的 Harmony 入口读取倍率。</summary>
    public static GameActions? Current { get; private set; }
    /// <summary>允许同时存在的本工具克隆上限。</summary>
    public int SpawnLimit { get; set; }
    /// <summary>出错时的游戏日志回调。</summary>
    public event Action<string>? Diagnostic;
    /// <summary>原生伤害入口使用的临时无敌。</summary>
    public bool Invincible => invincible;
    /// <summary>只在本次场景存在的实际抵伤护盾，不使用游戏已废弃的 healthBlue 字段。</summary>
    public int ShieldPoints => shieldPoints;
    /// <summary>仅本工具当前仍在场的友军。</summary>
    public int CompanionCount => companions.Count(item => item.Object != null && item.Object.activeSelf);
    public int AbsorbDamage(int damage)
    {
        var absorbed = Math.Min(shieldPoints, Math.Max(0, damage)); shieldPoints -= absorbed; return Math.Max(0, damage - absorbed);
    }
    /// <summary>正常致命伤害是否限制为剩余一格。</summary>
    public bool Survive => survive;
    /// <summary>承伤倍率。</summary>
    public double IncomingDamage => incomingDamage;
    /// <summary>攻击节奏倍率。</summary>
    public double AttackSpeed => attackSpeed;

    /// <summary>初始化主线程动作执行环境。</summary>
    public GameActions()
    {
        Current = this;
        effects.RestoreFailed += message => Diagnostic?.Invoke("恢复失败：" + message);
        diagnostics.Add("游戏程序集接口已匹配；各动作的实际战斗效果仍须联调。");
    }

    /// <summary>角色处于可操作且非过渡场景时允许战斗动作。</summary>
    public bool CanAct
    {
        get
        {
            var hero = ExistingHero(); var manager = ExistingManager(); // 心跳不能触发 Find、初始化回调或 DontDestroyOnLoad。
            return hero != null && manager != null && manager.IsGameplayScene() && !manager.isPaused &&
                !hero.cState.dead && !hero.cState.transitioning;
        }
    }

    /// <summary>推进临时效果、场景恢复和资源补充。</summary>
    public void Tick()
    {
        var scene = SceneManager.GetActiveScene().name; // 当前真实场景。
        var existingHero = ExistingHero(); // 只读缓存实例，避免引导期触发游戏初始化事件。
        if (scene != lastScene || existingHero != null && (existingHero.cState.dead || existingHero.cState.transitioning))
        {
            ResetForScene();
            lastScene = scene;
        }
        effects.Tick(Time.realtimeSinceStartup);
        appearanceStacks.RemoveAll(item => item == null);
        TickPickups();
        if(CanAct && Time.realtimeSinceStartup>=nextHostileScan){nextHostileScan=Time.realtimeSinceStartup+.25f;hostileCache=Enemies();}
        for (var index=companions.Count-1;index>=0;index--)
        {
            var lease=companions[index];
            if(lease.Object==null){spawnedOwners.Remove(lease.Id);companions.RemoveAt(index);continue;}
            if(CanAct && !float.IsPositiveInfinity(lease.RemainingSeconds))lease.RemainingSeconds-=Time.unscaledDeltaTime;
            if(lease.RemainingSeconds<=0){Object.Destroy(lease.Object);spawnedOwners.Remove(lease.Id);companions.RemoveAt(index);continue;}
            if(lease.Object.GetComponent<NativeEnemyAlly>()!=null && !CanAct){lease.Object.SetActive(false);continue;}
            if(!lease.Object.activeSelf && CanAct){lease.Object.transform.position=ExistingHero()!.transform.position+Vector3.up*1.5f;lease.Object.SetActive(true);}
        }
        for(var index=enemyTimers.Count-1;index>=0;index--)
        {
            var lease=enemyTimers[index];if(lease.Object==null){enemyTimers.RemoveAt(index);continue;}
            if(CanAct)lease.RemainingSeconds-=Time.unscaledDeltaTime;
            if(lease.RemainingSeconds<=0){spawned.Remove(lease.Object);spawnedOwners.Remove(lease.Id);Object.Destroy(lease.Object);enemyTimers.RemoveAt(index);}
        }
        spawned.RemoveAll(item => item == null);
        if (CanAct && Time.realtimeSinceStartup >= nextRefill)
        {
            nextRefill = Time.realtimeSinceStartup + 0.25f;
            if (infiniteSilk)
            {
                SetSilk(PlayerData.instance.CurrentSilkMax);
            }
            if (infiniteTools)
            {
                RefillTools();
            }
        }
    }

    /// <summary>执行一次指令，异步资源加载完成后才返回；恢复操作会使在途加载失效。</summary>
    /// <param name="request">经过共享校验的请求。</param>
    /// <returns>完成后的结果说明。</returns>
    public async Task<string> ExecuteAsync(ActionRequest request)
    {
        var errors = ActionValidation.Validate(request); // 双端校验，不能通过管道绕过。
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("；", errors));
        }
        ActionValidation.FillDefaults(request);
        var descriptor = ActionCatalog.All.First(item => item.Id == request.ActionId); // 动作说明。
        if (descriptor.RequiresGameplay && !CanAct)
        {
            throw new DeferredActionException("GAME_NOT_READY", "等待进入角色场景并结束菜单、死亡或场景过渡；此单位仍保留");
        }
        var hero = ExistingHero()!; // 非角色指令也不会创建角色实例。
        var data = ExistingPlayerData()!; // 不在引导场景创建存档单例。
        var value = Number(request, "value", 1); // 通用数值。
        var whole = (int)value; // 已验证的整数参数。
        if (MonsterCatalog.Find(request.ActionId) is { } monster) { return await SpawnNamedAsync(monster, request); }
        if (SkillCatalog.Find(request.ActionId) is { } skill)
        {
            CastSkill(hero, data, skill); exercised.Add(request.ActionId); return "已发起" + skill.Name;
        }
        switch (request.ActionId)
        {
            case "game.status": return "场景：" + SceneManager.GetActiveScene().name + "；生命：" + (data?.health ?? 0) + "；丝线：" + (data?.silk ?? 0);
            case "game.catalog": monsterAssets.Index(); return "原生资源目录已找到 " + monsterAssets.AvailableIds.Count + " / " + MonsterCatalog.All.Count + " 种具名怪物。";
            case "effects.reset": Reset(); break;
            case "game.capture":
                if (Time.realtimeSinceStartup < nextCapture) { throw new InvalidOperationException("截图冷却中，请等待五秒。"); }
                nextCapture = Time.realtimeSinceStartup + 5;
                var captureDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveInteract", "Captures"); // 仅保存游戏自身渲染帧。
                System.IO.Directory.CreateDirectory(captureDirectory);
                var captureFile = System.IO.Path.Combine(captureDirectory, "Silksong-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                ScreenCapture.CaptureScreenshot(captureFile);
                return "已请求游戏截图，等待渲染帧完成：" + captureFile;
            case "visual.brightness":
                var brightness = ExistingCameras()?.brightnessEffect ?? throw new InvalidOperationException("亮度组件尚未创建。");
                Temporary(request, "画面亮度:" + brightness.GetInstanceID(), value, false, () => brightness._Brightness, x => brightness.SetBrightness((float)x), () => brightness != null);
                break;
            case "visual.contrast":
                var contrast = ExistingCameras()?.brightnessEffect ?? throw new InvalidOperationException("对比度组件尚未创建。");
                Temporary(request, "画面对比:" + contrast.GetInstanceID(), value, false, () => contrast._Contrast, x => contrast.SetContrast((float)x), () => contrast != null);
                break;
            case "health.add": hero.AddHealth(whole); break;
            case "health.set": SetHealth(Math.Min(whole, data.CurrentMaxHealth)); break;
            case "health.full": hero.MaxHealth(); break;
            case "health.damage": hero.TakeDamage(hero.gameObject, GlobalEnums.CollisionSide.other, whole, GlobalEnums.HazardType.NON_HAZARD, default); break;
            case "health.kill": hero.TakeDamage(hero.gameObject, GlobalEnums.CollisionSide.other, Math.Max(9999, data.health + data.healthBlue + 100), GlobalEnums.HazardType.NON_HAZARD, default); break;
            case "health.blue": shieldPoints = Math.Min(999, shieldPoints + whole); return "已增加 " + whole + " 点临时护盾，当前 " + shieldPoints + " 点";
            case "health.max": data.maxHealthBase = whole; data.maxHealth = whole; SetHealth(Math.Min(data.health, data.CurrentMaxHealth)); break;
            case "health.invincible": Temporary(request, "无敌", 1, false, () => invincible ? 1 : 0, x => invincible = x > 0); break;
            case "health.survive": Temporary(request, "免死", 1, false, () => survive ? 1 : 0, x => survive = x > 0); break;
            case "silk.add": hero.AddSilk(whole, true); break;
            case "silk.set": SetSilk(whole); break;
            case "silk.full": SetSilk(data.CurrentSilkMax); break;
            case "silk.empty": SetSilk(0); break;
            case "silk.max": data.IsSilkSpoolBroken = false; data.silkMax = whole; hero.RefreshSilk(); break;
            case "silk.infinite": Temporary(request, "无限丝线", 1, false, () => infiniteSilk ? 1 : 0, x => infiniteSilk = x > 0); break;
            case "ally.clockwork": return await SpawnCompanionsAsync(request);
            case "ally.native.free-random": return await SpawnNativeAllyAsync(NativeSummons.FreeAlly(request.Id), request);
            case "ally.native.bone-goomba": case "ally.native.understore-thrower": case "ally.native.shellwood-gnat": case "ally.native.shellwood-wasp": case "ally.native.slab-fly-large":
                return await SpawnNativeAllyAsync(NativeSummons.Find(request.ActionId)!,request);
            case "summon.named.enemy": case "summon.named.ally":
                var namedMonster=SummonCommands.Resolve(Text(request,"name"));
                return await SpawnResolvedGroupAsync(new[]{namedMonster},request,request.ActionId.EndsWith(".ally",StringComparison.Ordinal),1);
            case "summon.random.enemy": case "summon.random.ally":
                var choices=Enumerable.Range(0,(int)Number(request,"count",1)).Select(i=>NativeSummons.RandomMonster(request.Id+"-"+i,Text(request,"pool")=="all")).ToArray();
                return await SpawnResolvedGroupAsync(choices,request,request.ActionId.EndsWith(".ally",StringComparison.Ordinal),Number(request,"power",1));
            case "boss.random":
                var selectedBoss=NativeSummons.Boss(request.Id);request.EffectText="随机Boss · "+selectedBoss.Name+" ×1";return await SpawnNamedAsync(selectedBoss,request);
            case "enemy.growStack": case "enemy.shrinkStack":
                var growTarget = SelectEnemy(Text(request, "enemy"));
                var layers = growTarget.GetComponents<AppearanceStack>().FirstOrDefault(item => !item.Retired) ?? growTarget.gameObject.AddComponent<AppearanceStack>();
                if (!appearanceStacks.Contains(layers)) { appearanceStacks.Add(layers); }
                layers.Add(DisplayNames.Clean(request.Triggerer), request.ActionId == "enemy.growStack", value, request.DurationSeconds);
                return (request.ActionId == "enemy.growStack" ? "变大" : "变小") + "独立叠层 " + request.DurationSeconds + "秒；当前 " + layers.Count + " 层，碰撞未改变";
            case "enemy.cloneSafe":
                var bossesNow = new HashSet<HealthManager>(BossSceneController.Instance?.bosses ?? Array.Empty<HealthManager>());
                var safeTarget = Enemies().Where(e => !bossesNow.Contains(e) && !MonsterCatalog.All.Any(m => m.IsBoss && e.name.StartsWith(m.ObjectName, StringComparison.Ordinal))).OrderBy(e => Vector3.Distance(e.transform.position, hero.transform.position)).FirstOrDefault()
                    ?? throw new DeferredActionException("TARGET_MISSING", "等待附近非Boss敌人；单位保留");
                Spawn(safeTarget.gameObject, request); break;
            case "effects.calm":
                foreach (var layer in appearanceStacks.ToArray()) { if (layer != null) { layer.Restore(); Object.Destroy(layer); } }
                appearanceStacks.Clear(); effects.RestorePrefix("敌人动画:"); animationSpeeds.Clear(); ClearHostiles();
                return "整活已恢复，敌对召唤已清理；友军和补给保留";
            case "enemy.clearHostiles": ClearHostiles(); return "敌对召唤已清理，友军保留";
            case "tools.refill": RefillTools(); break;
            case "tools.infinite": Temporary(request, "无限工具", 1, false, () => infiniteTools ? 1 : 0, x => infiniteTools = x > 0); break;
            case "tools.give":
                var tool = ToolItemManager.GetToolByName(Text(request, "tool")); // 实际工具资产。
                if (tool == null || !tool.IsUnlocked) { throw new InvalidOperationException("工具不存在或尚未解锁。"); }
                if (ToolItemManager.GetToolStorageAmount(tool) <= 0) { throw new InvalidOperationException("此工具是被动装备，没有可补充的数量；请选择消耗型工具。"); }
                tool.CollectFree(whole);
                break;
            case "tools.unlock":
                var unlock = ToolItemManager.GetToolByName(Text(request, "tool")); // 要解锁的工具。
                if (unlock == null) { throw new InvalidOperationException("未找到指定工具。"); }
                unlock.Unlock(null, default);
                break;
            case "tools.unlockAll": ToolItemManager.UnlockAllTools(); break;
            case "crest.unlockAll": ToolItemManager.UnlockAllCrests(); break;
            case "crest.equip":
                var crest = ToolItemManager.GetCrestByName(Text(request, "crest")); // 要装备的纹章。
                if (crest == null || !crest.IsUnlocked) { throw new InvalidOperationException("纹章不存在或尚未解锁。"); }
                ToolItemManager.SetEquippedCrest(crest.name);
                break;
            case "damage.all": Temporary(request, "总伤害", value, true, () => allDamage, x => allDamage = x); break;
            case "damage.needle": Temporary(request, "针伤害", value, true, () => needleDamage, x => needleDamage = x); break;
            case "damage.tools": Temporary(request, "工具伤害", value, true, () => toolDamage, x => toolDamage = x); break;
            case "damage.silk": Temporary(request, "丝技伤害", value, true, () => silkDamage, x => silkDamage = x); break;
            case "damage.taken": Temporary(request, "承伤", value, true, () => incomingDamage, x => incomingDamage = x); break;
            case "damage.knockback": Temporary(request, "击退", value, true, () => knockback, x => knockback = x); break;
            case "move.attackSpeed": Temporary(request, "攻击节奏", value, true, () => attackSpeed, x => attackSpeed = x); break;
            case "move.speed": Fields(request, hero, value, "RUN_SPEED", "WALK_SPEED", "QUICKENING_RUN_SPEED", "QUICKENING_WALK_SPEED"); break;
            case "move.jump": Fields(request, hero, value, "JUMP_SPEED", "MIN_JUMP_SPEED"); break;
            case "move.dash": Fields(request, hero, value, "DASH_SPEED"); break;
            case "move.gravity":
                Fields(request, hero, value, "DEFAULT_GRAVITY", "UNDERWATER_GRAVITY", "AIR_HANG_GRAVITY");
                var body = hero.GetComponent<Rigidbody2D>(); // 当前物理刚体。
                Temporary(request, "角色重力:" + body.GetInstanceID(), value, true, () => body.gravityScale, x => body.gravityScale = (float)x, () => body != null);
                break;
            case "move.freeze": Fields(request, hero, 0, "RUN_SPEED", "WALK_SPEED", "DASH_SPEED", "QUICKENING_RUN_SPEED", "QUICKENING_WALK_SPEED"); break;
            case "move.scale": Scale(request, hero.transform, value); break;
            case "move.jumpNow": AccessTools.Method(typeof(HeroController), "Jump").Invoke(hero, null); break;
            case "move.dashNow":
                if (!hero.CanInput()) { throw new DeferredActionException("HERO_BUSY", "角色正执行其他动作，等待后冲刺"); }
                AccessTools.Method(typeof(HeroController), "HeroDash", new[] { typeof(bool) }).Invoke(hero, new object[] { false });
                break;
            case "move.airReset": hero.ResetAirMoves(); break;
            case "skill.unlock": data.SetBool(Text(request, "ability"), Text(request, "enabled") == "true"); break;
            case "skill.cast": CastEvent(hero, Text(request, "fsm"), Text(request, "event")); break;
            case "enemy.spawn": case "enemy.clone": case "enemy.random": case "boss.clone":
                var enemies = Enemies(); // 只选择当前加载且存活的对象。
                HealthManager source; // 要复制的实际对象。
                if (request.ActionId == "enemy.random")
                {
                    if (enemies.Length == 0) { throw new DeferredActionException("TARGET_MISSING", "等待当前场景出现可复制的敌人"); }
                    source = enemies[UnityEngine.Random.Range(0, enemies.Length)];
                }
                else if (request.ActionId == "boss.clone" && string.IsNullOrWhiteSpace(Text(request, "enemy")))
                {
                    source = SelectBoss();
                }
                else
                {
                    source = SelectEnemy(Text(request, "enemy"));
                }
                Spawn(source.gameObject, request);
                break;
            case "enemy.asset": case "boss.asset":
                var key = Text(request, "asset");
                var namedAsset = MonsterCatalog.All.FirstOrDefault(item => item.Id == key || item.Asset == key || item.ObjectName == key || item.Name == key);
                if (namedAsset != null) { return await SpawnNamedAsync(namedAsset, request); } // 游戏自身资源键。
                if (string.IsNullOrWhiteSpace(key)) { throw new InvalidOperationException("请填写实际敌人或 Boss 资源 ID。"); }
                if (!Addressables.ResourceLocators.Any(locator => locator.Locate(key, typeof(GameObject), out _))) { throw new InvalidOperationException("游戏资源目录中不存在此预制体 ID。"); }
                var startedGeneration = generation; // 恢复或切场景使在途资源请求失效。
                var startedScene = SceneManager.GetActiveScene().name; // 加载发起的场景。
                var handle = Addressables.LoadAssetAsync<GameObject>(key); // 不下载或分发游戏资源。
                try
                {
                    var prefab = await handle.Task; // Unity 同步上下文回到主线程。
                    if (startedGeneration != generation || startedScene != SceneManager.GetActiveScene().name || !CanAct)
                    {
                        throw new DeferredActionException("SCENE_CHANGED", "加载期间状态改变；本单位尚未生成，等待角色恢复后再执行");
                    }
                    if (prefab == null || prefab.GetComponentInChildren<HealthManager>(true) == null)
                    {
                        throw new InvalidOperationException("目标资源不是带生命组件的敌人预制体。");
                    }
                    Spawn(prefab, request);
                    assetHandles.Add(handle);
                }
                catch
                {
                    if (handle.IsValid()) { Addressables.Release(handle); }
                    throw;
                }
                break;
            case "boss.heal":
                var healingBoss = SelectBoss(); healingBoss.hp = (int)Math.Min(100000000L, (long)healingBoss.hp + whole);
                return "当前 Boss 回复 " + whole + " 点生命，当前生命 " + healingBoss.hp;
            case "boss.damage":
                SelectBoss().ApplyExtraDamage(whole);
                break;
            case "enemy.heal":
                var healingEnemy = SelectEnemy(""); healingEnemy.hp = (int)Math.Min(100000000L, (long)healingEnemy.hp + whole);
                return "附近敌人回复 " + whole + " 点生命";
            case "enemy.health": SelectEnemy(Text(request, "enemy")).hp = whole; break;
            case "enemy.damage":
                SelectEnemy(Text(request, "enemy")).Hit(new HitInstance { DamageDealt = whole, Source = hero.gameObject, AttackType = AttackTypes.Generic, Multiplier = 1, IgnoreInvulnerable = true, IsManualTrigger = true });
                break;
            case "enemy.size": Scale(request, SelectEnemy(Text(request, "enemy")).transform, value); break;
            case "enemy.speed":
                var targetEnemy = SelectEnemy(Text(request, "enemy"));
                var animator = targetEnemy.GetComponentInChildren<Animator>();
                var spriteAnimator = targetEnemy.GetComponentInChildren<tk2dSpriteAnimator>();
                if (animator == null && spriteAnimator == null) { throw new InvalidOperationException("该敌人没有受支持的动画器。"); }
                if (animator != null) { Temporary(request, "敌人动画:" + animator.GetInstanceID(), value, true, () => animator.speed, x => animator.speed = (float)x, () => animator != null); }
                if (spriteAnimator != null)
                {
                    var id = spriteAnimator.GetInstanceID();
                    Temporary(request, "敌人动画:" + id, value, true, () => animationSpeeds.TryGetValue(id, out var speed) ? speed : 1, x => animationSpeeds[id] = x, () => spriteAnimator != null);
                }
                break;
            case "enemy.killAll": foreach (var enemy in Enemies()) { enemy.Die(null, AttackTypes.Generic, true); } break;
            case "enemy.clearSpawned": ClearSpawned(); break;
            case "currency.rain": return await RainCoinsAsync(request);
            case "currency.rosaries": if (whole >= 0) { hero.AddGeo(whole); } else { hero.TakeGeo(Math.Min(-whole, data.geo)); } break;
            case "currency.shards": if (whole >= 0) { hero.AddShards(whole); } else { hero.TakeShards(Math.Min(-whole, data.ShellShards)); } break;
            case "needle.upgrade": data.nailUpgrades = whole; break;
            case "position.mark": markedPosition = hero.transform.position; markedScene = SceneManager.GetActiveScene().name; break;
            case "position.return":
                if (markedPosition == null || markedScene != SceneManager.GetActiveScene().name) { throw new InvalidOperationException("本场景尚未记录位置，或记录来自其他场景。"); }
                hero.transform.position = markedPosition.Value;
                hero.GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero;
                break;
            case "position.move": hero.transform.position += new Vector3((float)Number(request, "x", 0), (float)Number(request, "y", 0), 0); break;
            case "scene.warp": Warp(Text(request, "scene"), Text(request, "gate")); break;
            case "scene.bench": Warp(data.respawnScene, data.respawnMarkerName); break;
            case "camera.zoom":
                var camera = ExistingCameras()?.tk2dCam; // 实际游戏镜头。
                if (camera == null) { throw new InvalidOperationException("当前游戏镜头尚未创建。"); }
                var zoom = camera.GetType().GetProperty("ZoomFactor") ?? throw new InvalidOperationException("本版本镜头不提供 ZoomFactor。"); // 已检测的缩放入口。
                Temporary(request, "镜头:" + camera.GetInstanceID(), value, true, () => Convert.ToDouble(zoom.GetValue(camera, null)), x => zoom.SetValue(camera, (float)x, null), () => camera != null);
                break;
            case "visual.hideHero":
                foreach (var renderer in hero.GetComponentsInChildren<Renderer>())
                {
                    var target = renderer; // 为每个渲染器保存自身原状态。
                    Temporary(request, "渲染:" + target.GetInstanceID(), 0, false, () => target.enabled ? 1 : 0, x => target.enabled = x > 0, () => target != null);
                }
                break;
            case "visual.hideHud":
                var hud = ExistingCameras()?.hudCamera ?? throw new InvalidOperationException("HUD 尚未创建。"); // 实际 HUD 摄像机。
                Temporary(request, "HUD:" + hud.GetInstanceID(), 0, false, () => hud.enabled ? 1 : 0, x => hud.enabled = x > 0, () => hud != null);
                break;
            case "time.scale": Temporary(request, "时间速度", value, false, () => Time.timeScale, x => Time.timeScale = (float)x); break;
            case "time.freeze": Temporary(request, "时间速度", 0, false, () => Time.timeScale, x => Time.timeScale = (float)x); break;
            default: throw new InvalidOperationException("动作未实现：" + request.ActionId);
        }
        exercised.Add(request.ActionId);
        return descriptor.Name + "：游戏端已执行。";
    }

    /// <summary>复用游戏原生机械飞行友军，不修改工具解锁或装备，不向角色发送操控事件。</summary>
    private Task<string> SpawnCompanionsAsync(ActionRequest request)
    {
        spawned.RemoveAll(item => item == null);
        var count = (int)Number(request, "count", 1);
        var template = Resources.FindObjectsOfTypeAll<ClockworkHatchling>()
            .Where(item => item != null && !item.name.Contains("[LiveInteract]"))
            .OrderBy(item => item.gameObject.scene.IsValid() ? 1 : 0).FirstOrDefault();
        if (template == null) { throw new DeferredActionException("ALLY_RESOURCE", "等待角色原生机械友军资源加载完成"); }
        if (staging == null) { staging = new GameObject("LiveInteract inactive spawn staging"); staging.SetActive(false); }
        if (companionRoot == null) { companionRoot = new GameObject("LiveInteract companions"); Object.DontDestroyOnLoad(companionRoot); }
        var lifetime = (float)Number(request, "lifetime", 20);
        for (var index = 0; index < count; index++)
        {
            var clone = Object.Instantiate(template.gameObject, staging.transform);
            clone.name = "机械小弟 [LiveInteract]";
            clone.AddComponent<OwnedCompanion>();
            var helper = clone.GetComponent<ClockworkHatchling>();
            var details = helper.normalDetails; details.damage = (int)Number(request, "value", 8); helper.normalDetails = details;
            AccessTools.Field(typeof(ClockworkHatchling), "toolSource").SetValue(helper, null);
            AccessTools.Field(typeof(ClockworkHatchling), "hasToolSource").SetValue(helper, false);
            AccessTools.Field(typeof(ClockworkHatchling), "hp").SetValue(helper, int.MaxValue);
            AccessTools.Field(typeof(ClockworkHatchling), "hpCurrent").SetValue(helper, 999);
            foreach (var hazard in clone.GetComponentsInChildren<DamageHero>(true)) { hazard.enabled = false; }
            clone.transform.position = ExistingHero()!.transform.position + new Vector3(-2 - index * 1.2f, 1.5f, 0);
            clone.transform.SetParent(companionRoot.transform, true);
            clone.transform.localScale = Vector3.one;
            clone.SetActive(true);
            var id = clone.GetInstanceID(); spawned.Add(clone);
            spawnedOwners[id] = DisplayNames.Clean(request.Triggerer.Length > 0 ? request.Triggerer : "主播");
            companions.Add(new CompanionLease { Object = clone, Id = id, RemainingSeconds = lifetime <= 0 ? float.PositiveInfinity : lifetime });
        }
        exercised.Add(request.ActionId);
        return Task.FromResult("已召唤原生机械友军 ×" + count + (lifetime <= 0 ? "，持续跟随并跨场景保留" : "，持续" + lifetime.ToString(CultureInfo.InvariantCulture) + "秒"));
    }

    /// <summary>先完成整批资源加载再创建；任何同步创建失败都撤销本次新对象，避免重试重复赠送。</summary>
    private async Task<string> SpawnResolvedGroupAsync(MonsterDefinition[] monsters,ActionRequest request,bool friendly,double power)
    {
        var started=generation;var handles=new List<AsyncOperationHandle<GameObject>>();
        foreach(var monster in monsters)
        {if(!namedPrefabHandles.TryGetValue(monster.Id,out var h)||!h.IsValid()){h=Addressables.LoadAssetAsync<GameObject>(monsterAssets.Locate(monster));namedPrefabHandles[monster.Id]=h;}handles.Add(h);}
        var load=Task.WhenAll(handles.Select(h=>h.Task));
        if(await Task.WhenAny(load,Task.Delay(6500))!=load)throw new DeferredActionException("ASSET_LOADING","整组原生资源仍在加载，尚未创建任何单位，继续等待");
        GameObject[] prefabs;
        try{prefabs=await load;}
        catch{foreach(var m in monsters.Distinct())if(namedPrefabHandles.TryGetValue(m.Id,out var h)&&h.IsValid()&&h.Status==AsyncOperationStatus.Failed){namedPrefabHandles.Remove(m.Id);Addressables.Release(h);}throw;}
        if(started!=generation||!CanAct)throw new DeferredActionException("SCENE_CHANGED","加载时场景改变，整组尚未创建，继续排队");
        if(prefabs.Any(p=>p==null||p.GetComponentInChildren<HealthManager>(true)==null))throw new InvalidOperationException("本组包含无效原生单位，没有创建替代怪物");
        if(!friendly && SpawnLimit>0 && spawned.Count(p=>p!=null&&p.GetComponent<OwnedCompanion>()==null)+monsters.Length>SpawnLimit)throw new DeferredActionException("SPAWN_LIMIT","等待敌对生成数量低于上限，整组未创建");
        if(friendly)foreach(var prefab in prefabs)NativeAllyExecution.Register(prefab);
        var before=new HashSet<int>(spawned.Where(p=>p!=null).Select(p=>p.GetInstanceID()));
        try
        {
            for(var i=0;i<monsters.Length;i++)
            {
                var args=new Dictionary<string,string>(request.Arguments){["count"]="1",["value"]=power.ToString(CultureInfo.InvariantCulture)};
                var part=new ActionRequest{Id=request.Id,ActionId=request.ActionId,Arguments=args,Triggerer=request.Triggerer,AllowPersistent=request.AllowPersistent};
                Spawn(prefabs[i],part,friendly?NativeSummons.Convert(monsters[i],power):null);
                if(!friendly && power>1 && !monsters[i].IsBoss)
                {
                    var created=spawned.Last();foreach(var hm in created.GetComponentsInChildren<HealthManager>(true))hm.hp=(int)Math.Min(100000,Math.Ceiling(hm.hp*power));
                    foreach(var damage in created.GetComponentsInChildren<DamageHero>(true))damage.damageDealt=Math.Min(2,Math.Max(1,(int)Math.Ceiling(damage.damageDealt*power)));
                }
            }
        }
        catch
        {
            var added=spawned.Where(p=>p!=null&&!before.Contains(p.GetInstanceID())).ToArray();var ids=new HashSet<int>(added.Select(p=>p.GetInstanceID()));
            foreach(var obj in added){spawnedOwners.Remove(obj.GetInstanceID());spawned.Remove(obj);Object.Destroy(obj);}companions.RemoveAll(l=>ids.Contains(l.Id));enemyTimers.RemoveAll(l=>ids.Contains(l.Id));throw;
        }
        var names=string.Join("、",monsters.GroupBy(m=>m.Id).Select(group=>group.First().Name+" ×"+group.Count()));var ttl=Number(request,"lifetime",0);
        request.EffectText=(power>1?"强力":"")+(friendly?"友军 ":"敌军 ")+names+(ttl>0?" · "+ttl+"秒":"");
        return "已召唤"+(friendly?"友方":"敌方")+names+"，数量"+monsters.Length;
    }

    /// <summary>实际落下GeoControl原生拾取物，分批生成避免一帧挤出整场金币雨。</summary>
    private async Task<string> RainCoinsAsync(ActionRequest request)
    {
        var candidates=Resources.FindObjectsOfTypeAll<GeoControl>().Where(c=>c!=null&&!c.name.Contains("[LiveInteract"));
        var template=candidates.Select(c=>new{Coin=c,Cost=AccessTools.Field(typeof(GeoControl),"valueReference").GetValue(c) as CostReference}).Where(x=>x.Cost!=null&&x.Cost.Value>0).OrderBy(x=>x.Coin.gameObject.scene.IsValid()?1:0).ThenBy(x=>x.Cost!.Value).FirstOrDefault();
        if(template==null)throw new DeferredActionException("COIN_RESOURCE","等待游戏原生念珠拾取物资源；未生成虚构金币，也没有直接加钱");
        var amount=(int)Number(request,"count",10);var spread=(float)Number(request,"spread",6);var height=(float)Number(request,"height",5);var epoch=stopEpoch;
        var random=new System.Random(NativeSummons.Choice(request.Id,int.MaxValue));
        for(var i=0;i<amount;i++)
        {
            while(!CanAct || !(bool)AccessTools.Property(typeof(CurrencyObjectBase),"CanPickupExist").GetValue(null,null)){if(epoch!=stopEpoch)throw new InvalidOperationException("金币雨已由主播停止，未落下部分取消");await Task.Delay(100);}
            if(epoch!=stopEpoch)throw new InvalidOperationException("金币雨已由主播停止，未落下部分取消");
            if(staging==null){staging=new GameObject("LiveInteract inactive spawn staging");staging.SetActive(false);}
            var hero=ExistingHero()!;var x=hero.transform.position.x+((float)random.NextDouble()-.5f)*spread;
            var origin=new Vector2(x,hero.transform.position.y+.7f);var y=hero.transform.position.y+height;
            var terrain=Physics2D.Raycast(origin,Vector2.up,height,LayerMask.GetMask("Terrain"));if(terrain.collider!=null)y=Math.Max(origin.y+.3f,terrain.point.y-.4f);
            var clone=Object.Instantiate(template.Coin.gameObject,staging.transform);clone.name="念珠 [LiveInteract Coin]";clone.AddComponent<OwnedRainPickup>();
            foreach(var persistent in clone.GetComponentsInChildren<Component>(true).Where(c=>c!=null&&(c.GetType().Name is "PersistentBoolItem" or "PersistentIntItem")))Object.DestroyImmediate(persistent);
            clone.transform.position=new Vector3(x,y,0);clone.transform.SetParent(null,true);clone.SetActive(true);
            var body=clone.GetComponent<Rigidbody2D>();if(body!=null)body.linearVelocity=new Vector2(((float)random.NextDouble()-.5f)*2,-1);
            rainCoins.Add(clone);await Task.Delay(25);
        }
        rainCoins.RemoveAll(c=>c==null||c.GetComponent<OwnedRainPickup>()?.StillOwned!=true);request.EffectText="原生念珠雨 ×"+amount;
        return "已落下"+amount+"颗原生念珠（每颗"+template.Cost!.Value+"），触碰拾取后由原作入账";
    }

    private async Task<string> SpawnNativeAllyAsync(NativeAllyDefinition ally,ActionRequest request)
    {
        var monster=MonsterCatalog.Find(ally.MonsterId)!;var started=generation;
        if(!namedPrefabHandles.TryGetValue(monster.Id,out var handle)||!handle.IsValid())
        {handle=Addressables.LoadAssetAsync<GameObject>(monsterAssets.Locate(monster));namedPrefabHandles[monster.Id]=handle;}
        if(await Task.WhenAny(handle.Task,Task.Delay(6500))!=handle.Task)throw new DeferredActionException("ASSET_LOADING","原生友军资源加载中，未生成的单位保留等待");
        GameObject prefab;
        try{prefab=await handle.Task;}
        catch{if(handle.IsValid()){Addressables.Release(handle);namedPrefabHandles.Remove(monster.Id);}throw;}
        if(started!=generation||!CanAct)throw new DeferredActionException("SCENE_CHANGED","加载期间切场景，本单位尚未创建，继续排队");
        if(prefab==null||prefab.GetComponentInChildren<HealthManager>(true)==null)throw new InvalidOperationException("原生友军资源未包含有效生命组件");
        Spawn(prefab,request,ally);exercised.Add(request.ActionId);request.EffectText=ally.Name+" ×"+request.Arguments["count"]+" · 持续跟随";
        return "已生成"+ally.Name+"，使用原生外观/状态机/攻击碰撞；转友方战斗兼容仍待实机确认";
    }

    /// <summary>具名和旧版资源入口共用正确的预制体加载与回执路径。</summary>
    private async Task<string> SpawnNamedAsync(MonsterDefinition monster, ActionRequest request)
    {
            var startedGeneration = generation;
            var startedScene = SceneManager.GetActiveScene().name;
            if (!namedPrefabHandles.TryGetValue(monster.Id, out var handle) || !handle.IsValid())
            {
                handle = Addressables.LoadAssetAsync<GameObject>(monsterAssets.Locate(monster)); namedPrefabHandles[monster.Id] = handle;
            }
            try
            {
                if (await Task.WhenAny(handle.Task, Task.Delay(6500)) != handle.Task)
                {
                    throw new DeferredActionException("ASSET_LOADING", "怪物资源仍在加载，本单位没有生成；完成加载后继续排队");
                }
                var prefab = await handle.Task;
                if (startedGeneration != generation || startedScene != SceneManager.GetActiveScene().name || !CanAct)
                {
                    throw new DeferredActionException("SCENE_CHANGED", "加载期间角色状态改变；本单位尚未生成，等待角色恢复后再执行");
                }
                if (prefab == null || prefab.GetComponentInChildren<HealthManager>(true) == null)
                {
                    throw new InvalidOperationException("当前资源不再是可生成的敌人；未创建对象。");
                }
                Spawn(prefab, request); exercised.Add(request.ActionId);
                return "已生成" + monster.Name + " ×" + request.Arguments["count"];
            }
            catch
            {
                if (handle.IsValid() && handle.IsDone && handle.Status == AsyncOperationStatus.Failed)
                {
                    namedPrefabHandles.Remove(monster.Id); Addressables.Release(handle);
                }
                throw;
            }
    }

    /// <summary>恢复临时属性并清除仅本工具创建的对象，不撤销永久修改。</summary>
    public void Reset()
    {
        stopEpoch++;
        foreach(var coin in rainCoins)if(coin!=null&&coin.GetComponent<OwnedRainPickup>()?.StillOwned==true)Object.Destroy(coin);rainCoins.Clear();
        generation++;
        effects.RestoreAll(); animationSpeeds.Clear(); shieldPoints = 0;
        foreach (var layer in appearanceStacks.ToArray()) { if (layer != null) { layer.Restore(); Object.Destroy(layer); } }
        appearanceStacks.Clear(); pickups.Clear(); ClearSpawned();
    }

    /// <summary>场景转换只恢复临时改动；援军和待拾取补给继续跟随。</summary>
    public void ResetForScene()
    {
        generation++; effects.RestoreAll(); animationSpeeds.Clear(); shieldPoints = 0;
        foreach (var layer in appearanceStacks.ToArray()) { if (layer != null) { layer.Restore(); Object.Destroy(layer); } }
        appearanceStacks.Clear(); ClearHostiles(); relocatePickups = true;
    }
    private void ClearHostiles()
    {
        foreach (var item in spawned.Where(item => item != null && item.GetComponent<OwnedCompanion>() == null).ToArray())
        { spawnedOwners.Remove(item.GetInstanceID()); Object.Destroy(item); spawned.Remove(item); }
    }
    private void TickPickups()
    {
        if (!CanAct) { return; }
        var hero = ExistingHero()!; var data = ExistingPlayerData()!;
        if (relocatePickups)
        {
            for (var i = 0; i < pickups.Count; i++) { pickups[i].Position = hero.transform.position + new Vector3(2.6f + i % 3 * .4f, .7f, 0); pickups[i].Born = Time.realtimeSinceStartup; }
            relocatePickups = false;
        }
        for (var index = pickups.Count - 1; index >= 0; index--)
        {
            var p = pickups[index]; var need = p.Kind == "pickup.health" ? data.health < data.CurrentMaxHealth : data.silk < data.CurrentSilkMax;
            if (!need || Time.realtimeSinceStartup - p.Born < .7f) { continue; }
            var destination = hero.transform.position + Vector3.up * .5f;
            if (Time.realtimeSinceStartup - p.Born > 4) { p.Position = Vector3.MoveTowards(p.Position, destination, 5 * Time.unscaledDeltaTime); }
            if (Vector3.Distance(p.Position, destination) > .75f) { continue; }
            var grant = Math.Min(p.Amount, p.Kind == "pickup.health" ? data.CurrentMaxHealth - data.health : data.CurrentSilkMax - data.silk);
            if (p.Kind == "pickup.health") { hero.AddHealth(grant); } else { hero.AddSilk(grant, true); }
            p.Amount -= grant;
            if (p.Amount <= 0) { pickups.RemoveAt(index); }
            PickupCollected?.Invoke(p.Who, "拾取" + (p.Kind == "pickup.health" ? "生命" : "灵丝") + "补给 +" + grant);
        }
    }
    /// <summary>捕获纯数据心跳，不在后台线程调用 Unity。</summary>
    /// <returns>当前游戏状态。</returns>
    public GameStatus CaptureStatus()
    {
        var data = ExistingPlayerData(); // 只读已有数据，禁止为心跳创建单例。
        var status = new GameStatus
        {
            Scene = SceneManager.GetActiveScene().name, GameVersion = Application.version, ModVersion = "0.6.0", CanAct = CanAct,
            Health = data?.health ?? 0, BlueHealth = data?.healthBlue ?? 0, ShieldPoints = shieldPoints, CompanionCount = CompanionCount, PickupCount = pickups.Count, SizeLayerCount = appearanceStacks.Where(a => a != null).Sum(a => a.Count), Silk = data?.silk ?? 0,
            ActiveEffects = effects.Describe(Time.realtimeSinceStartup).ToList(),
            SpawnableMonsterIds = monsterAssets.AvailableIds,
            Diagnostics = diagnostics.Concat(exercised.Select(item => "已执行：" + item)).ToList()
        };
        if (CompanionCount > 0) { status.ActiveEffects.Add("跟随援军 ×" + CompanionCount); }
        if (CanAct)
        {
            if (!indexedResources)
            {
                indexedResources = true;
                foreach (var locator in Addressables.ResourceLocators)
                {
                    foreach (var rawKey in locator.Keys)
                    {
                        if (rawKey is not string key || key.Length > 300 || key.Length == 32 && key.All(Uri.IsHexDigit)) { continue; }
                        if (sceneKeys.Count < 2000 && locator.Locate(key, typeof(SceneInstance), out _)) { sceneKeys.Add(key); }
                        if (prefabKeys.Count < 3000 && (key.IndexOf("enem", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0) && locator.Locate(key, typeof(GameObject), out _)) { prefabKeys.Add(key); }
                    }
                }
                diagnostics.Add("资源候选目录已读取；敌人和 Boss 的跨场景行为仍需逐项实测。");
            }
            status.SceneIds = sceneKeys.Distinct().OrderBy(key => key).ToList();
            status.EnemyAssetIds = prefabKeys.Distinct().OrderBy(key => key).ToList();
            status.EnemyIds = Enemies().Select(item => item.name + "#" + item.GetInstanceID()).Distinct().OrderBy(item => item).ToList();
            status.ToolIds = ToolItemManager.GetAllTools().Where(item => item != null).Select(item => item.name).Distinct().OrderBy(item => item).ToList();
            status.CrestIds = ToolItemManager.GetAllCrests().Where(item => item != null).Select(item => item.name).Distinct().OrderBy(item => item).ToList();
            foreach (var fsmName in new[] { "spellControl", "silkSpecialFSM", "toolsFSM" })
            {
                var fsm = AccessTools.Field(typeof(HeroController), fsmName)?.GetValue(HeroController.SilentInstance) as PlayMakerFSM; // 白名单状态机。
                if (fsm != null && fsm.Fsm?.Events != null)
                {
                    status.SkillEvents.AddRange(fsm.Fsm.Events.Select(item => fsmName + ":" + item.Name));
                }
            }
        }
        return status;
    }

    /// <summary>修改玩家对敌人的实际伤害与击退，不改写存档伤害字段。</summary>
    /// <param name="hit">命中结构。</param>
    public void AdjustHit(ref HitInstance hit)
    {
        if (!hit.IsHeroDamage && (hit.Source == null || hit.Source.GetComponent<DamageEnemies>() == null && hit.Source != ExistingHero()?.gameObject))
        {
            return;
        }
        var specific = hit.IsNailDamage ? needleDamage : hit.RepresentingTool != null ? toolDamage : silkDamage; // 按实际来源分组。
        hit.DamageDealt = (int)Math.Min(100000000, Math.Max(0, Math.Round(hit.DamageDealt * allDamage * specific)));
        hit.MagnitudeMultiplier *= (float)knockback;
    }

    /// <summary>为临时数值登记恢复回调。</summary>
    /// <param name="request">当前请求。</param>
    /// <param name="key">属性键。</param>
    /// <param name="value">效果值。</param>
    /// <param name="multiply">是否乘法。</param>
    /// <param name="read">基础值读取。</param>
    /// <param name="write">属性写入。</param>
    /// <param name="alive">目标是否有效。</param>
    private void Temporary(ActionRequest request, string key, double value, bool multiply, Func<double> read, Action<double> write, Func<bool>? alive = null) => effects.Apply(key, value, request.DurationSeconds, request.Stacking, multiply, read, write, Time.realtimeSinceStartup, alive);

    /// <summary>成组修改已核实的角色浮点字段。</summary>
    /// <param name="request">当前请求。</param>
    /// <param name="hero">角色。</param>
    /// <param name="value">倍率。</param>
    /// <param name="names">固定字段名。</param>
    private void Fields(ActionRequest request, HeroController hero, double value, params string[] names)
    {
        foreach (var name in names)
        {
            var field = AccessTools.Field(typeof(HeroController), name) ?? throw new MissingFieldException(name); // 已核实字段。
            Temporary(request, name + ":" + hero.GetInstanceID(), value, true, () => Convert.ToDouble(field.GetValue(hero)), x => field.SetValue(hero, (float)x), () => hero != null);
        }
    }

    /// <summary>tk2d 动画采用增量时间倍率，换动作时不会丢失效果。</summary>
    public float AnimationSpeed(tk2dSpriteAnimator animator) => animationSpeeds.TryGetValue(animator.GetInstanceID(), out var multiplier) ? (float)multiplier : 1;

    /// <summary>即时资源效果可直接核对的数值，满值或被抵挡不伪装成实际增加。</summary>
    public string? ResourceEvidence(string action)
    {
        var data = ExistingPlayerData(); if (data == null) { return null; }
        if (action == "health.blue") { return shieldPoints.ToString(); }
        if (new[] { "health.add", "health.set", "health.full", "health.damage", "health.kill", "health.max", "health.blue" }.Contains(action)) { return data.health + ":" + data.maxHealth + ":" + data.healthBlue + ":" + shieldPoints; }
        if (new[] { "silk.add", "silk.set", "silk.full", "silk.empty", "silk.max" }.Contains(action)) { return data.silk + ":" + data.CurrentSilkMax; }
        if (action == "currency.rosaries") { return data.geo.ToString(); }
        if (action == "currency.shards") { return data.ShellShards.ToString(); }
        if (action == "needle.upgrade") { return data.nailUpgrades.ToString(); }
        return null;
    }

    /// <summary>只投影本工具生成对象的名字，不扫描全场景。</summary>
    public IEnumerable<SpawnLabel> SpawnLabels()
    {
        foreach (var obj in spawned)
        {
            if (obj == null || !obj.activeInHierarchy || !spawnedOwners.TryGetValue(obj.GetInstanceID(), out var name)) { continue; }
            var collider = obj.GetComponentInChildren<Collider2D>();
            var visible = obj.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && !r.forceRenderingOff).ToArray();
            var point = visible.Length > 0 ? new Vector3(obj.transform.position.x, visible.Max(r => r.bounds.max.y) + .35f, obj.transform.position.z) : collider != null ? new Vector3(collider.bounds.center.x, collider.bounds.max.y + .35f, obj.transform.position.z) : obj.transform.position + Vector3.up * 1.5f;
            yield return new SpawnLabel { Name = name, Position = point, IsFriendly = obj.GetComponent<OwnedCompanion>() != null };
        }
        foreach (var layer in appearanceStacks.Where(item => item != null))
        {
            var collider = layer.GetComponentInChildren<Collider2D>(); var point = collider != null ? new Vector3(collider.bounds.center.x, collider.bounds.max.y + .4f, 0) : layer.transform.position + Vector3.up;
            var row = 0; foreach (var text in layer.Labels()) { yield return new SpawnLabel { Name = text, Position = point + Vector3.up * row++ * .4f, IsFriendly = true }; }
        }
    }

    /// <summary>分轴保存物体基础比例。</summary>
    /// <param name="request">当前请求。</param>
    /// <param name="target">目标。</param>
    /// <param name="value">倍率。</param>
    private void Scale(ActionRequest request, Transform target, double value)
    {
        for (var index = 0; index < 2; index++)
        {
            var axis = index; // 闭包保存坐标轴。
            Temporary(request, "比例:" + target.GetInstanceID() + ":" + axis, value, true, () => Math.Abs(target.localScale[axis]), x =>
            {
                var scale = target.localScale; // 仅替换对应轴，保留其他轴的效果。
                scale[axis] = (scale[axis] < 0 ? -1 : 1) * (float)x;
                target.localScale = scale;
            }, () => target != null);
        }
    }

    /// <summary>调用角色更新接口设置生命。</summary>
    /// <param name="value">目标生命。</param>
    private static void SetHealth(int value)
    {
        var current = PlayerData.instance.health; // 当前值。
        if (value >= current) { HeroController.SilentInstance.AddHealth(value - current); }
        else { HeroController.SilentInstance.TakeHealth(current - value); }
    }

    /// <summary>读取角色静态缓存，不触发游戏的实例发现回调。</summary>
    /// <returns>已创建角色或空。</returns>
    private static HeroController? ExistingHero() => HeroInstanceField.GetValue(null) as HeroController;

    /// <summary>读取游戏管理器静态缓存，不改变对象生命周期。</summary>
    /// <returns>已创建管理器或空。</returns>
    private static GameManager? ExistingManager() => ManagerInstanceField.GetValue(null) as GameManager;

    /// <summary>读取摄像机缓存，不主动搜索或初始化镜头。</summary>
    /// <returns>已创建摄像机或空。</returns>
    private static GameCameras? ExistingCameras() => CamerasInstanceField.GetValue(null) as GameCameras;

    /// <summary>只读取已存在的玩家数据，避免引导场景提前初始化并触发 UI 引用错误。</summary>
    /// <returns>游戏已创建的数据或空。</returns>
    private static PlayerData? ExistingPlayerData() => AccessTools.Field(typeof(PlayerData), "_instance").GetValue(null) as PlayerData;

    /// <summary>通过游戏实际接口修改丝线并刷新显示。</summary>
    /// <param name="value">目标丝线。</param>
    private static void SetSilk(int value)
    {
        var data = PlayerData.instance; // 当前丝线数据。
        value = Math.Max(0, Math.Min(value, data.CurrentSilkMax));
        if (value >= data.silk) { HeroController.SilentInstance.AddSilk(value - data.silk, true); }
        else { HeroController.SilentInstance.TakeSilk(data.silk - value); }
        HeroController.SilentInstance.RefreshSilk();
    }

    /// <summary>补充已解锁工具，不改变解锁状态。</summary>
    private static void RefillTools()
    {
        foreach (var tool in ToolItemManager.GetUnlockedTools())
        {
            tool.CollectFree(Math.Max(1, tool.BaseStorageAmount));
        }
    }

    /// <summary>找到当前场景有效且非玩家的生命组件。</summary>
    /// <returns>当前场景敌人。</returns>
    private static HealthManager[] Enemies() => Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None).Where(item => item != null && !item.isDead && item.hp > 0 && !item.IsPartOfSendToTarget && item.gameObject.scene.IsValid() && item.GetComponentInParent<HeroController>() == null && item.GetComponentInParent<OwnedCompanion>() == null).ToArray();

    /// <summary>选择指定对象或最近敌人。</summary>
    /// <param name="name">内部名称或名称加实例号。</param>
    /// <returns>实际敌人对象。</returns>
    private static HealthManager SelectEnemy(string name)
    {
        var candidates = Enemies().AsEnumerable(); // 当前有效对象。
        if (!string.IsNullOrWhiteSpace(name))
        {
            candidates = candidates.Where(item => item.name == name || item.name + "#" + item.GetInstanceID() == name);
        }
        return candidates.OrderBy(item => Vector3.Distance(item.transform.position, HeroController.SilentInstance.transform.position)).FirstOrDefault() ?? throw new DeferredActionException("TARGET_MISSING", "等待当前场景出现指定敌人，不消耗本次单位");
    }

    /// <summary>只选择明确的当前 Boss 引用或本工具生成的具名 Boss，不用血量猜测。</summary>
    private HealthManager SelectBoss()
    {
        var bosses = (BossSceneController.Instance?.bosses ?? Array.Empty<HealthManager>()).AsEnumerable();
        var ownBosses = spawned.Where(obj => obj != null && MonsterCatalog.All.Any(m => m.IsBoss && obj.name == m.ObjectName + " [LiveInteract]"))
            .SelectMany(obj => obj.GetComponentsInChildren<HealthManager>());
        return bosses.Concat(ownBosses).Where(item => item != null && item.gameObject.activeInHierarchy && !item.isDead && item.hp > 0)
            .OrderBy(item => Vector3.Distance(item.transform.position, ExistingHero()!.transform.position)).FirstOrDefault()
            ?? throw new DeferredActionException("BOSS_MISSING", "等待实际 Boss 出现，不会把普通敌人当作 Boss");
    }

    /// <summary>检查实际解锁和灵丝，再调用原生丝技状态机。</summary>
    private static void CastSkill(HeroController hero, PlayerData data, SilkSkill skill)
    {
        var fsm = hero.silkSpecialFSM;
        if (!hero.CanInput() || fsm == null || fsm.ActiveStateName != "Idle") { throw new DeferredActionException("SKILL_BUSY", "等待角色结束当前动作后施放" + skill.Name); }
        if (data.disableSilkAbilities) { throw new DeferredActionException("SILK_DISABLED", "当前剧情 / 状态暂时禁用丝技，等待恢复"); }
        var original = data.silk;
        if (data.silk < data.SilkSkillCost) { SetSilk(data.SilkSkillCost); }
        try { CastEvent(hero, "silkSpecialFSM", skill.Event); }
        catch { if (original < data.SilkSkillCost) { SetSilk(original); } throw; }

    }

    /// <summary>在非激活父物体下准备克隆，移除独立存档组件后再启用。</summary>
    /// <param name="source">场景对象或已加载预制体。</param>
    /// <param name="request">生成参数。</param>
    private void Spawn(GameObject source, ActionRequest request, NativeAllyDefinition? ally = null)
    {
        spawned.RemoveAll(item => item == null);
        var count = (int)Number(request, "count", 1); // 本次生成数量。
        if (ally == null && SpawnLimit > 0 && spawned.Count(item => item.GetComponent<OwnedCompanion>() == null) + count > SpawnLimit)
        {
            throw new DeferredActionException("SPAWN_LIMIT", "等待已有生成怪物被击败 / 清理，当前同时生成上限 " + SpawnLimit);
        }
        if (staging == null)
        {
            staging = new GameObject("LiveInteract inactive spawn staging");
            staging.SetActive(false);
        }
        NativeAllyExecution.Register(source);
        for (var index = 0; index < count; index++)
        {
            var clone = Object.Instantiate(source, staging.transform); spawned.Add(clone); // 未激活时先清理持久化组件。
            clone.name = source.name + " [LiveInteract]"; clone.AddComponent<OwnedSummon>();
            foreach (var component in clone.GetComponentsInChildren<Component>(true).Where(item => item != null && (item.GetType().Name == "PersistentBoolItem" || item.GetType().Name == "PersistentIntItem")))
            {
                Object.DestroyImmediate(component);
            }
            foreach (var health in clone.GetComponentsInChildren<HealthManager>(true))
            {
                AccessTools.Field(typeof(HealthManager), "sendKilledToObject")?.SetValue(health, null);
                AccessTools.Field(typeof(HealthManager), "sendKilledToName")?.SetValue(health, "");
                AccessTools.Field(typeof(HealthManager), "battleScene")?.SetValue(health, null);
            }
            foreach (var death in clone.GetComponentsInChildren<EnemyDeathEffects>(true))
            {
                death.setPlayerDataBool = ""; death.doNotSetHasKilled = true; death.DisableSpecialQuestDrops();
                AccessTools.Field(typeof(EnemyDeathEffects), "awardAchievement").SetValue(death, "");
                AccessTools.Field(typeof(EnemyDeathEffects), "sendEventRegister").SetValue(death, "");
                AccessTools.Field(typeof(EnemyDeathEffects), "journalRecord").SetValue(death, null);
            }
            clone.transform.position = HeroController.SilentInstance.transform.position + new Vector3((float)Number(request, "offsetX", 3) + index * 1.2f, (float)Number(request, "offsetY", 1), 0);
            if(ally!=null)
            {
                if(companionRoot==null){companionRoot=new GameObject("LiveInteract companions");Object.DontDestroyOnLoad(companionRoot);}
                clone.name=ally.Name+" [LiveInteract]";clone.AddComponent<OwnedCompanion>();
                NativeAllyExecution.Register(source);
                var controller=clone.AddComponent<NativeEnemyAlly>();controller.PreserveNativeMovement=MonsterCatalog.Find(ally.MonsterId)?.IsBoss==true;controller.Flying=ally.Flying;controller.DamageMultiplier=(float)Number(request,"value",ally.DamageMultiplier);
                clone.transform.SetParent(companionRoot.transform,true);
                var lifetime=(float)Number(request,"lifetime",0);companions.Add(new CompanionLease{Object=clone,Id=clone.GetInstanceID(),RemainingSeconds=lifetime<=0?float.PositiveInfinity:lifetime});
            }
            else clone.transform.SetParent(null, true);
            if(ally==null && Number(request,"lifetime",0)>0)enemyTimers.Add(new CompanionLease{Object=clone,Id=clone.GetInstanceID(),RemainingSeconds=(float)Number(request,"lifetime",0)});
            clone.SetActive(true);
            spawnedOwners[clone.GetInstanceID()] = DisplayNames.Clean(request.Triggerer.Length > 0 ? request.Triggerer : "主播");
        }
    }

    /// <summary>清理只属于本工具的对象与持有资源。</summary>
    private void ClearSpawned()
    {
        foreach (var item in spawned)
        {
            if (item != null) { Object.Destroy(item); }
        }
        spawned.Clear(); spawnedOwners.Clear(); companions.Clear(); enemyTimers.Clear();
        if (companionRoot != null) { Object.Destroy(companionRoot); companionRoot = null; }
        foreach (var handle in assetHandles)
        {
            if (handle.IsValid()) { Addressables.Release(handle); }
        }
        assetHandles.Clear();
        if (staging != null) { Object.Destroy(staging); staging = null; }
    }

    /// <summary>只有插件结束时释放预制体引用；清理克隆不能卸载仍被游戏缓存的动画和材质。</summary>
    public void DisposeResources()
    {
        Reset();
        foreach (var handle in namedPrefabHandles.Values) { if (handle.IsValid()) { Addressables.Release(handle); } }
        namedPrefabHandles.Clear();
    }

    /// <summary>向白名单战斗状态机的实际事件发送指令。</summary>
    /// <param name="hero">当前角色。</param>
    /// <param name="field">固定状态机字段。</param>
    /// <param name="eventName">实际事件名。</param>
    private static void CastEvent(HeroController hero, string field, string eventName)
    {
        var fsm = AccessTools.Field(typeof(HeroController), field)?.GetValue(hero) as PlayMakerFSM; // 已经由目录限制字段名。
        if (fsm == null || !fsm.Fsm.Events.Any(item => item.Name == eventName))
        {
            throw new InvalidOperationException("该战斗状态机没有指定事件，请从游戏状态目录选择。");
        }
        var before = fsm.ActiveStateName; // 发送前状态。
        fsm.SendEvent(eventName);
        if (fsm.ActiveStateName == before)
        {
            throw new InvalidOperationException("事件已送达，但当前状态没有发生切换；可能尚未解锁、资源不足或时机不符。");
        }
    }

    /// <summary>使用游戏自身的场景入口，拒绝不存在的资源。</summary>
    /// <param name="scene">目标场景。</param>
    /// <param name="gate">入口名。</param>
    private static void Warp(string scene, string gate)
    {
        var target = (scene ?? "").Trim().Replace('\\', '/');
        if (target.StartsWith("Scenes/", StringComparison.Ordinal)) { target = target.Substring(7); }
        if (target.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) { target = target.Substring(0, target.Length - 6); }
        var key = "Scenes/" + target;
        if (string.IsNullOrWhiteSpace(target) || !Addressables.ResourceLocators.Any(locator => locator.Locate(key, typeof(SceneInstance), out _)) && SceneUtility.GetBuildIndexByScenePath(target) < 0)
        {
            throw new InvalidOperationException("未找到目标场景资源：" + key + "，未执行传送。");
        }
        GameManager.SilentInstance.BeginSceneTransition(new GameManager.SceneLoadInfo { SceneName = target, EntryGateName = gate, EntryDelay = 0, PreventCameraFadeOut = false });
    }

    /// <summary>读取已验证的数值参数。</summary>
    /// <param name="request">请求。</param>
    /// <param name="key">参数键。</param>
    /// <param name="fallback">可选参数默认值。</param>
    /// <returns>数值。</returns>
    private static double Number(ActionRequest request, string key, double fallback) => request.Arguments.TryGetValue(key, out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;

    /// <summary>读取文本参数。</summary>
    /// <param name="request">请求。</param>
    /// <param name="key">参数键。</param>
    /// <returns>文本或空字符串。</returns>
    private static string Text(ActionRequest request, string key) => request.Arguments.TryGetValue(key, out var text) ? text : "";
}

/// <summary>怪物头顶的纯数据姓名位置。</summary>
internal sealed class SpawnLabel
{
    public string Name { get; set; } = "";
    public Vector3 Position { get; set; }
    public bool IsFriendly { get; set; }
}

/// <summary>原生友军的本次场景租期，不保存到游戏存档。</summary>
internal sealed class CompanionLease
{
    public GameObject Object { get; set; } = null!;
    public int Id { get; set; }
    public float RemainingSeconds { get; set; }
}

/// <summary>可见、可拾取且可跨场景保留的应援补给。</summary>
internal sealed class SupportPickup
{
    public string Kind = "", Who = ""; public int Amount; public Vector3 Position; public float Born;
}

/// <summary>原生拾取物首次回收后放弃所有权，避免紧急恢复误删已被原生对象池复用的金币。</summary>
internal sealed class OwnedRainPickup : MonoBehaviour
{
    public bool StillOwned=true;
    private void OnDisable()=>StillOwned=false;
}

/// <summary>仅标记本工具生成的原生单位，用于阻止召唤物的剧情状态写回，不改变原场景敌人。</summary>
internal sealed class OwnedSummon : MonoBehaviour { }
