#if LIVEINTERACT_QA
using System;
using System.Collections;
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
internal sealed class QaHarness
{
    private readonly BaseUnityPlugin owner;
    private readonly GameActions actions;
    private readonly ModPipeClient bridge;
    private readonly Harmony protection = new("cn.liveinteract.qa.protection");
    private string isolatedRoot = "";
    private Vector3? anchor;
    private bool isolated;
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
                case "state": break;
                default: throw new InvalidOperationException("未知 QA 操作：" + operation);
            }
            result.Success = true; result.Message = Snapshot().ToString(Formatting.None);
        }
        catch (Exception exception) { result.Message = exception.ToString(); }
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
            var suppress = new HarmonyMethod(typeof(QaHarness), nameof(Suppress));
            foreach (var name in new[] { "PushAchievementUnlock", "UpdateAchievementProgress", "ResetAchievements" })
            {
                protection.Patch(AccessTools.Method(typeof(DesktopPlatform), name), prefix: suppress);
            }
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
        actions.Reset(); var hero = HeroController.SilentInstance; var data = PlayerData.instance;
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
        return snapshot;
    }

    /// <summary>测试档禁止真实成就或共享偏好写回。</summary>
    private static bool Suppress() => false;

    /// <summary>正常退出自己启动的测试游戏，等待回执发送后由 Unity 关闭。</summary>
    private IEnumerator QuitAfterReply()
    {
        yield return new WaitForSecondsRealtime(1);
        Application.Quit();
    }
}
#endif
