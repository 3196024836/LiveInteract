using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>主线程执行、可核对回执和简洁的渐隐触发提示。</summary>
[BepInPlugin("cn.liveinteract.silksong", "直播互动工具箱", "0.7.6")]
[BepInProcess("Hollow Knight Silksong.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private ModPipeClient bridge = null!;
    private GameActions actions = null!;
#if LIVEINTERACT_QA
    private QaHarness qa = null!;
#endif
    private Harmony harmony = null!;
    private ConfigEntry<bool> overlayEnabled = null!;
    private ConfigEntry<int> overlayFontSize = null!;
    private readonly Dictionary<string, ActionResult> completed = new();
    private readonly HashSet<string> executing = new();
    private readonly Queue<string> completedOrder = new();
    private readonly ConcurrentQueue<ActionRequest> abandoned = new();
    private readonly ConcurrentQueue<string> diagnostics = new();
    private readonly FadingNoticeQueue notices = new();
    private readonly ReceiptStore receiptStore = new();
    private float nextHeartbeat, menuInfoUntil;
    private Font? font;
    private GUIStyle? label;
    private GUIStyle? ownerLabel;
    private bool stopping, wasConnected;
    private int tickFailures;
    private Color textColor = new(0.85f, 0.98f, 0.96f);
    private float noticeSeconds = 5;
    private string lastScene = "";
    private string lastAction = "";
    private readonly Dictionary<string, float> lastUnityError = new();
    private GameStatus snapshot = new();

    private void Awake()
    {
        Application.runInBackground = true;
        overlayEnabled = Config.Bind("Overlay", "Enabled", true, "游戏内简洁触发提示与怪物归属名");
        overlayFontSize = Config.Bind("Overlay", "FontSize", 18, "触发提示字号");
        actions = new GameActions(); actions.Diagnostic += text => diagnostics.Enqueue(text);
        actions.PickupCollected += (who, text) => notices.Add(Time.realtimeSinceStartup, who, text, true, noticeSeconds);
        actions.PendingSpawned += (who, text) => notices.Add(Time.realtimeSinceStartup, who, text, true, noticeSeconds);
        bridge = new ModPipeClient(); bridge.Diagnostic += text => diagnostics.Enqueue(text); bridge.RequestAbandoned += request => abandoned.Enqueue(request);
#if LIVEINTERACT_QA
        qa = new QaHarness(this, actions, bridge);
#endif
        harmony = new Harmony("cn.liveinteract.silksong");
        // 逐个补丁类应用：某个补丁对不上时只记录，不让整个 Mod 加载失败。
        foreach (var type in typeof(Plugin).Assembly.GetTypes().Where(item => item.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0))
        {
            try { harmony.PatchAll(type); }
            catch (Exception error) { Logger.LogError("补丁未应用：" + type.Name + "：" + error.Message); }
        }
        Application.logMessageReceived += OnUnityMessage;
        menuInfoUntil = Time.realtimeSinceStartup + 6;
        Logger.LogInfo("直播互动 Mod 0.7.6 已加载；待游戏与直播实际验证。");
    }

    private void Update()
    {
        while (diagnostics.TryDequeue(out var diagnostic)) { Logger.LogWarning(diagnostic); }
        if (bridge.ConsumeDisconnect()) { actions.ResetForScene(); }
        while (abandoned.TryDequeue(out var request))
        {
            Remember(new ActionResult { RequestId = request.Id, ErrorCode = "NOT_APPLIED", Retryable = true, Message = "连接断开前尚未执行，可安全重新排队" });
        }
        // 单帧异常不能让整个插件停摆：否则死亡 / 过场后连接、心跳和友军维护都会一起停。
        try { actions.Tick(); tickFailures = 0; }
        catch (Exception exception) { if (++tickFailures is 1 or 30 or 300) { Logger.LogError("场景处理失败：" + exception); } }
        for (var n = 0; n < 10 && bridge.TryRead(out var message); n++)
        {
            if (message.Kind == "reset") { actions.Reset(); }
            else if (message.Kind == "settings")
            {
                var options = JObject.Parse(message.Text);
                overlayEnabled.Value = (bool?)options["enabled"] ?? overlayEnabled.Value;
                var size = Math.Max(12, Math.Min(36, (int?)options["fontSize"] ?? 18));
                if (size != overlayFontSize.Value) { overlayFontSize.Value = size; label = null; ownerLabel = null; }
                if (ColorUtility.TryParseHtmlString((string?)options["accent"] ?? "#D9F9EF", out var parsed)) { textColor = parsed; }
                noticeSeconds = Math.Max(1, Math.Min(30, (float?)options["seconds"] ?? 5));
                actions.SpawnLimit = Math.Max(0, (int?)options["spawnLimit"] ?? 0);
            }
            else if (message.Kind == "receipt") { ReplyReceipt(message.Text); }
#if LIVEINTERACT_QA
            else if (message.Kind == "qa") { qa.Handle(message.Text); }
#endif
            else if (message.Kind == "action" && message.Request != null) { HandleRequest(message.Request); }
        }
        if (Time.realtimeSinceStartup >= nextHeartbeat)
        {
            nextHeartbeat = Time.realtimeSinceStartup + 0.25f;
            try
            {
                snapshot = actions.CaptureStatus();
#if LIVEINTERACT_QA
                snapshot.DiagnosticSession = qa.IsIsolated ? "isolated-QA" : "";
#endif
                bridge.Send(new() { Kind = "status", Status = snapshot });
                if (snapshot.Scene != lastScene)
                {
                    if (snapshot.Scene.Contains("Menu")) { menuInfoUntil = Time.realtimeSinceStartup + 5; }
                    lastScene = snapshot.Scene;
                }
                if (bridge.Connected != wasConnected)
                {
                    wasConnected = bridge.Connected; if (snapshot.Scene.Contains("Menu")) { menuInfoUntil = Time.realtimeSinceStartup + 4; }
                }
            }
            catch (Exception exception) { Logger.LogError("心跳读取失败：" + exception); }
        }
    }

    /// <summary>空闲战斗画面不绘制固定框，只绘制短时触发列表和已生成怪物的名字。</summary>
    private void OnGUI()
    {
        if (label == null)
        {
            font ??= Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, overlayFontSize.Value);
            label = new GUIStyle(GUI.skin.label) { font = font, fontSize = overlayFontSize.Value, wordWrap = true, richText = false };
            ownerLabel = new GUIStyle(label) { fontSize = Math.Max(12, overlayFontSize.Value - 3), alignment = TextAnchor.LowerCenter, wordWrap = false };
        }
        var now = Time.realtimeSinceStartup;
        if (snapshot.Scene.Contains("Menu"))
        {
            if (now < menuInfoUntil) { DrawText(new Rect(22, Screen.height - 48, 560, 30), "互动 Mod 已加载 · " + (bridge.Connected ? "控制台已连接" : "等待控制台"), textColor, label); }
            return;
        }

        if (!overlayEnabled.Value) { return; }
        var rows = notices.Read(now);
        var lineHeight = overlayFontSize.Value + 12;
        // 待生成队列固定显示在左下角提示上方，不随时间消失。
        var pending = actions.PendingLabels().ToList();
        for (var index = 0; index < pending.Count; index++)
        {
            var y = Screen.height - 24 - (rows.Count + pending.Count - index) * lineHeight;
            DrawText(new Rect(22, y, Math.Min(900, Screen.width - 44), lineHeight + 4), pending[index], new Color(1f, 0.85f, 0.45f), label);
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var item = rows[index]; var color = item.Success ? textColor : new Color(1, 0.73f, 0.5f); color.a = (float)item.Alpha;
            var y = Screen.height - 24 - (rows.Count - index) * lineHeight;
            DrawText(new Rect(22, y, Math.Min(900, Screen.width - 44), lineHeight + 4), item.Triggerer + " · " + item.Effect, color, label);
        }
        var camera = Camera.main;
        if (camera != null)
        {
            var occupied = new List<Rect>();
            foreach (var item in actions.SpawnLabels())
            {
                var point = camera.WorldToScreenPoint(item.Position);
                if (point.z <= 0 || point.x < -100 || point.x > Screen.width + 100 || point.y < 0 || point.y > Screen.height) { continue; }
                var width = Math.Min(320, Math.Max(60, ownerLabel!.CalcSize(new GUIContent(item.Name)).x + 12));
                var rect = new Rect(Math.Max(2, Math.Min(Screen.width - width - 2, point.x - width / 2)), Screen.height - point.y - 27, width, 26);
                while (rect.y > 2 && occupied.Any(previous => previous.Overlaps(rect))) { rect.y -= 26; }
                occupied.Add(rect); DrawText(rect, item.Name, item.IsFriendly ? new Color(0.65f, 1, 0.84f) : Color.white, ownerLabel);
            }
        }
    }
    /// <summary>真实世界位置的补给球，角色触碰/吸附后才结算；不需要接管任何按键。</summary>
    private static void DrawText(Rect rect, string text, Color color, GUIStyle style)
    {
        var previous = GUI.color; GUI.color = new Color(0, 0, 0, color.a * 0.8f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
        GUI.color = color; GUI.Label(rect, text, style); GUI.color = previous;
    }
    private void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityMessage;
        stopping = true; actions?.DisposeResources(); bridge?.Dispose(); harmony?.UnpatchSelf(); NativeAllyExecution.Dispose();
    }

    /// <summary>原生货币队列在 Update 才到账；等完整一帧后再判断，不能提前误报无变化。</summary>
    private Task AfterCurrencyUpdate()
    {
        var ready = new TaskCompletionSource<bool>(); StartCoroutine(WaitForCurrency(ready)); return ready.Task;
    }
    private static IEnumerator WaitForCurrency(TaskCompletionSource<bool> ready)
    {
        yield return null; yield return new WaitForEndOfFrame(); ready.TrySetResult(true);
    }

    private async void HandleRequest(ActionRequest request)
    {
        if (completed.TryGetValue(request.Id, out var old) && !old.Retryable) { bridge.Send(new() { Kind = "result", Result = old }); return; }
        if (!executing.Add(request.Id)) { return; }
        lastAction = (request.Triggerer.Length > 0 ? request.Triggerer + " · " : "") + ActionPresentation.Describe(request);
        var watch = Stopwatch.StartNew(); var result = new ActionResult { RequestId = request.Id };
        try
        {
            var before = actions.ResourceEvidence(request.ActionId);
            result.Message = await actions.ExecuteAsync(request); result.Success = true;
            if (request.ActionId is "currency.rosaries" or "currency.shards") { await AfterCurrencyUpdate(); }
            var after = actions.ResourceEvidence(request.ActionId);
            if (before != null && before == after)
            {
                result.ErrorCode = "NO_CHANGE"; result.Message += "（数值没有变化，可能已满或被当前状态抵挡）";
            }
        }
        catch (DeferredActionException exception) { result.Message = exception.Message; result.ErrorCode = exception.Code; result.Retryable = true; }
        catch (Exception exception)
        {
            var actual = exception is System.Reflection.TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : exception;
            result.Message = actual.Message; result.ErrorCode = "GAME_ERROR"; Logger.LogWarning(request.ActionId + "：" + actual);
        }
        finally { executing.Remove(request.Id); }
        result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
        if (stopping) { return; }
        Remember(result);
        if (!result.Retryable)
        {
            var text = request.EffectText.Length > 0 ? request.EffectText : ActionPresentation.Describe(request);
            if (!result.Success) { text += " · 未完成"; }
            else if (result.ErrorCode == "NO_CHANGE") { text += " · 无变化"; }
            notices.Add(Time.realtimeSinceStartup, request.Triggerer.Length > 0 ? request.Triggerer : "主播", text, result.Success, noticeSeconds);
        }
    }
    private void Remember(ActionResult result)
    {
        if (!completed.ContainsKey(result.RequestId)) { completedOrder.Enqueue(result.RequestId); }
        completed[result.RequestId] = result;
        while (completedOrder.Count > 100000) { completed.Remove(completedOrder.Dequeue()); }
        try { receiptStore.Save(result); }
        catch (Exception exception) { Logger.LogError("回执未能落盘，结果只能在当前进程核对：" + exception); bridge.Send(new() { Kind = "diagnostic", Text = "游戏回执未能落盘：" + exception.Message }); }
        bridge.Send(new() { Kind = "result", Result = result });
    }
    private void OnUnityMessage(string condition, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
        var now = Time.realtimeSinceStartup;
        if (lastUnityError.TryGetValue(condition, out var previous) && now - previous < 3) { return; }
        if (lastUnityError.Count > 256) { lastUnityError.Clear(); }
        lastUnityError[condition] = now;
        var detail = "游戏错误" + (lastAction.Length > 0 ? " · 最近指令 " + lastAction : "") + "：" + condition;
        if (stack.Length > 0) { detail += "\n" + string.Join("\n", stack.Split('\n').Take(5)); }
        bridge.Send(new() { Kind = "diagnostic", Text = detail.Length > 4000 ? detail.Substring(0, 4000) : detail });
    }

    private async void ReplyReceipt(string id)
    {
        ActionResult? result;
        try { if (!completed.TryGetValue(id, out result)) { result = await receiptStore.FindAsync(id); } }
        catch (Exception exception) { result = new() { RequestId = id, ErrorCode = "UNKNOWN", Message = "读取回执失败：" + exception.Message }; }
        if (stopping) { return; }
        bridge.Send(new() { Kind = "result", Result = result ?? new ActionResult { RequestId = id, ErrorCode = "NOT_FOUND", Message = "没有找到该指令回执，不能据此判断未执行；需人工核对" } });
    }
}
