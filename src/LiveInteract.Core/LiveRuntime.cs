using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveInteract.Contracts;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>一条本机记录，来源和结果可以筛选；昵称独立于规则名称。</summary>
public sealed record RuntimeEntry(DateTimeOffset At, string Kind, string Message, bool Success = true, LiveEvent? SourceEvent = null, string Triggerer = "", LiveEventType? SourceType = null, string JobId = "", string ErrorCode = "")
{
    public string SourceText => SourceType switch { LiveEventType.Chat => "弹幕", LiveEventType.Gift => "礼物", LiveEventType.Like => "点赞", LiveEventType.Follow => "关注", LiveEventType.Enter => "进场", LiveEventType.Share => "分享", LiveEventType.FanClub => "粉丝团", _ => "系统" };
    public string StateText => Kind switch { "Received" => "待评估", "Queued" => "已排队", "Unmatched" => "未命中条件 / 未到门槛", "Paused" => "收到时已暂停", "Duplicate" => "重复已抑制", "NeedsReview" => "待核对", "Cancelled" => "主播已取消", _ => Kind };
}

/// <summary>事务化入站、可恢复队列和顺序执行的会话所有者，不用内存溢出换取丢消息。</summary>
public sealed class LiveRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(1);
    private readonly ConcurrentBag<CancellationTokenSource> retired = new();
    private readonly RuleEngine engine = new(reliable: true);
    private readonly DouyinPacketDecoder decoder = new();
    private readonly BilibiliPacketDecoder bilibili = new();
    private readonly ConcurrentDictionary<LiveEventType, long> sourceCounts = new();
    private long capturedFrames, decodedFrames;
    private readonly ActionExecutor executor;
    private readonly IGameAdapter adapter;
    private readonly object engineGate = new();
    private readonly object logGate = new();
    private readonly StreamWriter? logWriter;
    private readonly Task[] workers;
    private CancellationTokenSource currentActions = new();
    private volatile bool enabled;
    private long received, simulated, completed, failed, unchanged;
    private RuleProfile profile = new();
    private string profileId = "";
    private bool disposing;

    /// <summary>本机桥与持久账本。</summary>
    public GamePipeServer Game { get; }
    public InteractionJournal Journal { get; }
    public RuleProfile Profile => profile;
    public string ProfileId => Volatile.Read(ref profileId);
    public bool Enabled => enabled;
    public bool AllowPersistent { get; set; }
    public bool HideNicknames { get; set; }
    public long Received => Interlocked.Read(ref received);
    public long Simulated => Interlocked.Read(ref simulated);
    public long Completed => Interlocked.Read(ref completed);
    public long Failed => Interlocked.Read(ref failed);
    public long Unchanged => Interlocked.Read(ref unchanged);
    public long CapturedFrames => Interlocked.Read(ref capturedFrames);
    public long DecodedFrames => Interlocked.Read(ref decodedFrames);
    public long CountForType(LiveEventType type) => sourceCounts.GetValueOrDefault(type);
    public int Pending => (int)Math.Min(int.MaxValue, Journal.PendingUnits);
    public event Action<RuntimeEntry>? EntryAdded;
    public event Action<ActionRequest, ActionResult>? ActionCompleted;

    /// <summary>预览及测试可使用内存账本；正式桌面必须传入本用户数据目录。</summary>
    public LiveRuntime(bool startBridge = true, string? dataDirectory = null, IGameAdapter? testAdapter = null, bool runWorkers = true)
    {
        Journal = new InteractionJournal(dataDirectory == null ? ":memory:" : Path.Combine(dataDirectory, "interactions.db"));
        Journal.RecoverAfterRestart(); engine.LoadState(Journal.EngineState); engine.ResetSession(); decoder.LoadState(Journal.DecoderState);
        decoder.Warning += message => Log("协议诊断", message, false);
        bilibili.Warning += message => Log("协议诊断", message, false);
        Game = new GamePipeServer(); adapter = testAdapter ?? Game;
        executor = new ActionExecutor(adapter, () => AllowPersistent);
        if (dataDirectory != null)
        {
            var directory = Path.Combine(dataDirectory, "Logs"); Directory.CreateDirectory(directory);
            logWriter = new StreamWriter(new FileStream(Path.Combine(directory, "runtime-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
        Game.Diagnostic += message => Log("连接", message, !message.Contains("错误", StringComparison.Ordinal));
        executor.ActionStarted += request => Journal.RecordIntent(request);
        executor.ActionCompleted += (request, result) =>
        {
            Journal.RecordResult(request, result);
            if (result.Success && result.ErrorCode == "NO_CHANGE") { Interlocked.Increment(ref unchanged); }
            else if (result.Success) { Interlocked.Increment(ref completed); }
            else if (!result.Retryable) { Interlocked.Increment(ref failed); }
            var name = ActionCatalog.All.FirstOrDefault(item => item.Id == request.ActionId)?.Name ?? request.ActionId;
            Log(result.Retryable ? "等待条件" : result.Success ? result.ErrorCode == "NO_CHANGE" ? "无变化" : "已生效" : result.ErrorCode == "UNKNOWN" ? "待核对" : "失败", (request.Triggerer.Length > 0 ? request.Triggerer + " · " : "") + name + " · " + result.Message, result.Success || result.Retryable, null, request.Triggerer, request.SourceType, request.JobId, result.ErrorCode);
            ActionCompleted?.Invoke(request, result);
        };
        if (startBridge) { Game.Start(); }
        workers = runWorkers ? [Task.Run(ProcessInputsAsync), Task.Run(ProcessJobsAsync), Task.Run(ProcessFramesAsync)] : [];
    }

    /// <summary>接收时固定配置版本，不让之后的编辑改变已收礼物的效果。</summary>
    public void ApplyProfile(RuleProfile value)
    {
        var copy = ProfileStore.Read(JsonConvert.SerializeObject(value), false);
        Volatile.Write(ref profile, copy); Volatile.Write(ref profileId, Journal.RegisterProfile(copy));
        Log("配置", "已应用 " + copy.Rules.Count + " 条规则，启用 " + copy.Rules.Count(item => item.Enabled) + " 条。"); Wake();
    }
    public void SetEnabled(bool value)
    {
        enabled = value;
        Log("控制", value ? "互动已开启；已入队任务继续处理。" : "暂停新触发；已入队任务保留，不删除礼物记录。"); Wake();
    }

    /// <summary>同步确认落盘后返回；不在内存满时丢输入。</summary>
    public void Submit(LiveEvent value) => SubmitBatch([value], enabled || value.IsSimulation, ProfileId);
    public int SubmitBatch(IEnumerable<LiveEvent> values, bool eligible, string? capturedProfile = null)
    {
        var list = values.ToArray();
        try
        {
            var count = Journal.AdmitEvents(list, eligible, capturedProfile ?? ProfileId);
            if (count > 0)
            {
                if (list.All(value => value.IsSimulation)) { Interlocked.Add(ref simulated, count); }
                else { Interlocked.Add(ref received, count); }
                Wake();
            }
            return count;
        }
        catch (Exception exception)
        {
            enabled = false; Log("存储故障", "未能保存收到的互动，已停止接收，请检查磁盘 / 权限：" + exception.Message, false); throw;
        }
    }

    /// <summary>浏览器数据在回调返回前落盘，后台解码不依赖 UI 消息队列容量。</summary>
    public long CaptureFrame(byte[] payload, string room)
    {
        var id = Journal.AppendFrame(payload, room, enabled, ProfileId); Interlocked.Increment(ref capturedFrames); return id;
    }

    /// <summary>明确模拟输入仍通过真正游戏执行路径，不能假报成功。</summary>
    public Task<bool> TestActionAsync(ActionStep step) => executor.ExecuteAsync(step, "主播手动测试", AllowPersistent, currentActions.Token, new() { Triggerer = "主播" });
    public int ResumePending()
    {
        var count = Journal.ResumePending(); Log("恢复", "已恢复 " + count + " 项未完成任务。待核对的指令没有自动重放。"); Wake(); return count;
    }
    /// <summary>核对选中的未知结果；只在游戏能证明已经生效 / 明确未执行时调整单位。</summary>
    public async Task CheckUnknownAsync(string jobId)
    {
        var command = Journal.UnknownCommand(jobId);
        if (command == null) { Log("核对", "没有可查询的在途指令记录；请在游戏中人工核对。", false); return; }
        var result = await Game.QueryReceiptAsync(command.Id, shutdown.Token);
        if (result.ErrorCode is "UNKNOWN" or "NOT_FOUND" or "NOT_CONNECTED") { Log("待核对", result.Message, false, null, command.Triggerer, null, jobId, result.ErrorCode); return; }
        Journal.ResolveUnknown(jobId, result); Log("回执已核对", command.Triggerer + " · " + result.Message, result.Success || result.Retryable, null, command.Triggerer, null, jobId, result.ErrorCode); Wake();
    }

    public async Task EmergencyStopAsync()
    {
        enabled = false; Journal.CancelPending("主播明确执行紧急停止，取消未发出的单位");
        var old = Interlocked.Exchange(ref currentActions, new CancellationTokenSource()); old.Cancel(); retired.Add(old);
        if (Game.IsConnected)
        {
            try { await Game.ResetAsync(shutdown.Token); }
            catch (IOException exception) { Log("恢复", exception.Message, false); }
        }
        Log("控制", "紧急停止：未发出的任务已取消并保留记录；已发出而未确认的任务需要核对。永久修改不会自动回滚。");
    }
    public (long Accumulated, int Triggered) RuleProgress(string ruleId)
    {
        lock (engineGate) { return (engine.AccumulatedCount(ruleId), engine.TriggeredCount(ruleId)); }
    }
    public void Log(string kind, string message, bool success = true, LiveEvent? sourceEvent = null, string triggerer = "", LiveEventType? sourceType = null, string jobId = "", string errorCode = "")
    {
        var entry = new RuntimeEntry(DateTimeOffset.Now, kind, message, success, sourceEvent, triggerer.Length > 0 ? triggerer : sourceEvent?.Nickname ?? "", sourceType ?? sourceEvent?.Type, jobId, errorCode);
        if (logWriter != null)
        {
            try { lock (logGate) { logWriter.WriteLine(JsonConvert.SerializeObject(entry)); } }
            catch (IOException) { enabled = false; }
        }
        EntryAdded?.Invoke(entry);
    }

    /// <summary>关闭程序保留未发出的任务，和明确的紧急清空不是同一个操作。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposing) { return; } disposing = true; enabled = false;
        currentActions.Cancel(); shutdown.Cancel(); changed.Writer.TryComplete();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
        // 不发送 reset：那会取消游戏端排队召唤并清场，只有紧急停止才这样做。
        await Game.DisposeAsync();
        currentActions.Dispose(); foreach (var item in retired) { item.Dispose(); }
        lock (logGate) { logWriter?.Dispose(); }
        Journal.Dispose(); shutdown.Dispose();
    }

    private async Task ProcessFramesAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                StoredFrame? frame;
                try { frame = Journal.ReadFrame(); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 存储暂时故障不能让解帧线程静默退出；记录后稍后重试。
                    Log("存储故障", "读取原始帧失败，1 秒后重试：" + exception.Message, false); await Task.Delay(1000, shutdown.Token); continue;
                }
                if (frame == null) { await Task.Delay(50, shutdown.Token); continue; }
                try
                {
                    // 房间标识带平台前缀时按平台选择解析器；原始帧和回执仍然共用同一套账本。
                    var isBilibili = frame.Room.StartsWith("bili:", StringComparison.Ordinal);
                    var values = isBilibili
                        ? bilibili.DecodeFrame(frame.Payload, frame.Room, frame.Id.ToString())
                        : decoder.DecodeFrame(frame.Payload, frame.Room, frame.Id.ToString());
                    var state = isBilibili ? Journal.DecoderState : decoder.SaveState();
                    var count = Journal.CommitFrame(frame, values, state);
                    Interlocked.Add(ref received, count); Interlocked.Increment(ref decodedFrames);
                    foreach (var value in values) { sourceCounts.AddOrUpdate(value.Type, 1, (_, old) => old + 1); }
                    Wake();
                }
                catch (Exception exception)
                {
                    if (!frame.Room.StartsWith("bili:", StringComparison.Ordinal)) { decoder.LoadState(Journal.DecoderState); }
                    Journal.FailFrame(frame.Id, exception.ToString());
                    Log("接入待核对", "帧 " + frame.Id + " 无法解析，原始数据已保留：" + exception.Message, false);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private async Task ProcessInputsAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var input = Journal.ReadPendingEvents(1).FirstOrDefault();
                if (input == null)
                {
                    if (!await changed.Reader.WaitToReadAsync(shutdown.Token)) { break; }
                    while (changed.Reader.TryRead(out _)) { }
                    continue;
                }
                try
                {
                    lock (engineGate) { Journal.ProcessNext(engine, profile); }
                    var entry = Journal.InspectInput(input.Event);
                    Log(entry.StateText, entry.Message, entry.Success, entry.SourceEvent);
                }
                catch (Exception exception)
                {
                    enabled = false; Log("输入错误", "消息仍保存在本机账本，处理停止：" + exception.Message, false, input.Event); await Task.Delay(1000, shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
    private async Task ProcessJobsAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                if (!adapter.IsConnected) { await Task.Delay(100, shutdown.Token); continue; }
                try { await ProcessOneJobAsync(); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 账本读写故障不能让执行线程静默退出；记录后稍后重试。
                    Log("存储故障", "读取或更新执行队列失败，1 秒后重试：" + exception.Message, false); await Task.Delay(1000, shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    /// <summary>取出并执行一个待执行单位；条件未满足时延后。</summary>
    /// <returns>处理完成任务。</returns>
    private async Task ProcessOneJobAsync()
    {
        var job = Journal.AcquireNext(!enabled);
        if (job == null) { await Task.Delay(40, shutdown.Token); return; }
        var descriptor = job.Root.Kind == StepKind.Action ? ActionCatalog.All.FirstOrDefault(a => a.Id == job.Root.ActionId) : null;
        if (!adapter.IsConnected || (descriptor?.RequiresGameplay != false && adapter.Status?.CanAct != true))
        {
            Journal.Defer(job.Id, !adapter.IsConnected ? "等待游戏重新连接，数量已保留" : "等待进入角色场景 / 结束暂停、死亡或过场"); await Task.Delay(25, shutdown.Token); return;
        }
        if (HasPersistent(job.Root) && !AllowPersistent)
        {
            Journal.Defer(job.Id, "等待主播允许本次会话的永久修改"); return;
        }
        var context = JsonConvert.DeserializeObject<ActionContext>(JsonConvert.SerializeObject(job.Context))!;
        context.JobId = job.Id; context.UnitIndex = job.UnitIndex; context.TriggerIndex = (int)Math.Min(int.MaxValue, job.UnitIndex + 1); context.Triggerer = job.Triggerer; context.SourceType = job.SourceType; context.RequestIdPrefix = job.Id + "-" + job.UnitIndex;
        try
        {
            var success = await executor.ExecuteAsync(job.Root, job.Triggerer + " · " + job.RuleName + (job.IsSimulation ? " [模拟输入]" : ""), AllowPersistent, currentActions.Token, context);
            var result = Journal.LastResult(job.Id, job.UnitIndex) ?? new ActionResult { Success = success, Message = success ? "已完成" : "执行前校验失败，详情见运行日志", ErrorCode = success ? "" : "VALIDATION" };
            Journal.CompleteUnit(job.Id, result);
        }
        catch (OperationCanceledException)
        {
            Journal.CompleteUnit(job.Id, new() { ErrorCode = "UNKNOWN", Message = "执行中被中断，结果未知；需核对，不自动重放" });
        }
        catch (Exception exception)
        {
            Journal.CompleteUnit(job.Id, new() { ErrorCode = "UNKNOWN", Message = "执行期间异常，结果未知：" + exception.Message });
            Log("执行异常", job.Triggerer + " · " + job.Effect + " · " + exception.Message, false, null, job.Triggerer, job.SourceType, job.Id, "UNKNOWN");
        }
    }
    private static bool HasPersistent(ActionStep step) => step.Kind == StepKind.Action ? ActionCatalog.All.Any(a => a.Id == step.ActionId && a.IsPersistent) : step.Children.Any(HasPersistent);
    private void Wake() => changed.Writer.TryWrite(true);
}
