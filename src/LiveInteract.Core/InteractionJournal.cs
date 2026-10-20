using LiveInteract.Contracts;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>可筛选的实际排队任务；每个礼物单位有结果，不把数量展开成易溢出的内存数组。</summary>
public sealed class InteractionJob
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Triggerer { get; set; } = "";
    public string UserId { get; set; } = "";
    public LiveEventType SourceType { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset LocalAt => At.ToLocalTime();
    public ActionStep Root { get; set; } = new();
    public long Total { get; set; }
    public long Succeeded { get; set; }
    public long NoChange { get; set; }
    public long Failed { get; set; }
    public long Unknown { get; set; }
    public long Cancelled { get; set; }
    public long Remaining => Math.Max(0, Total - Succeeded - NoChange - Failed - Unknown - Cancelled);
    public long UnitIndex => Total - Remaining;
    public string State { get; set; } = "Queued";
    public string Reason { get; set; } = "";
    public string Effect => RuleDisplay.Effect(Root);
    public string Progress => (Total - Remaining) + " / " + Total + " · 成功 " + Succeeded + " · 无变化 " + NoChange + " · 失败 " + Failed + " · 待核对 " + Unknown + " · 取消 " + Cancelled;
    public string StateText => State switch { "Queued" => "排队中", "Preparing" or "Running" => "执行中", "Stopping" => "正在停止", "Waiting" => "等待条件", "Succeeded" => "已生效", "NoChange" => "已执行 / 无变化", "Failed" => "失败", "Unknown" => "结果待核对", "UnknownStopped" => "待核对 / 余量已取消", "Cancelled" => "已取消", "AwaitingResume" => "等待恢复", _ => State };
    public string SourceText => SourceType switch { LiveEventType.Gift => "礼物", LiveEventType.Chat => "弹幕", LiveEventType.Like => "点赞", LiveEventType.Follow => "关注", LiveEventType.Enter => "进场", LiveEventType.Share => "分享", _ => "粉丝团" };
    public bool IsSimulation { get; set; }
    public double CooldownSeconds { get; set; }
    public double UserCooldownSeconds { get; set; }
    public ActionContext Context { get; set; } = new();
}

/// <summary>浏览器已经收到且落盘的帧，不包含 Cookie 或请求头。</summary>
public sealed record StoredFrame(long Id, byte[] Payload, string Room, bool Eligible, string ProfileId, DateTimeOffset At);
/// <summary>已落盘的观众输入及当时的互动开关 / 配置版本。</summary>
public sealed record StoredInput(LiveEvent Event, bool Eligible, string ProfileId);

/// <summary>SQLite WAL 本地入站与任务账本；事件、连击增量、规则状态和入队在同一事务提交。</summary>
public sealed class InteractionJournal : IDisposable
{
    private readonly SqliteConnection database;
    private readonly object gate = new();
    private SqliteTransaction? transaction;

    /// <summary>打开当前用户私有账本。</summary>
    public InteractionJournal(string file)
    {
        if (file != ":memory:") { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!); }
        database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()); database.Open();
        Sql("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; " +
            "CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, json TEXT NOT NULL, eligible INTEGER NOT NULL, profile TEXT NOT NULL, state TEXT NOT NULL, reason TEXT NOT NULL, normalized TEXT, at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY,json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS meta(id TEXT PRIMARY KEY,value TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS combos(id TEXT PRIMARY KEY,quantity INTEGER NOT NULL,ended INTEGER NOT NULL,at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS frames(id INTEGER PRIMARY KEY AUTOINCREMENT,payload BLOB NOT NULL,room TEXT NOT NULL,eligible INTEGER NOT NULL,profile TEXT NOT NULL,at TEXT NOT NULL,done INTEGER NOT NULL DEFAULT 0,error TEXT NOT NULL DEFAULT '');" +
            "CREATE TABLE IF NOT EXISTS jobs(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,event_id TEXT NOT NULL,rule_id TEXT NOT NULL,who TEXT NOT NULL,source INTEGER NOT NULL,json TEXT NOT NULL,state TEXT NOT NULL,reason TEXT NOT NULL,total INTEGER NOT NULL,success INTEGER NOT NULL DEFAULT 0,nochange INTEGER NOT NULL DEFAULT 0,failed INTEGER NOT NULL DEFAULT 0,unknown INTEGER NOT NULL DEFAULT 0,cancelled INTEGER NOT NULL DEFAULT 0,next REAL NOT NULL DEFAULT 0,simulation INTEGER NOT NULL DEFAULT 0);" +
            "CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,job TEXT NOT NULL,unit INTEGER NOT NULL,request TEXT NOT NULL,result TEXT,state TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS throttles(id TEXT PRIMARY KEY,at REAL NOT NULL);" +
            "CREATE INDEX IF NOT EXISTS ix_events_state ON events(state); CREATE INDEX IF NOT EXISTS ix_jobs_state_next ON jobs(state,next,seq); CREATE INDEX IF NOT EXISTS ix_frames_done ON frames(done,id);");
    }

    /// <summary>唯一输入计数；重放不增加。</summary>
    public long ReceivedCount { get { lock (gate) { return Number("SELECT COUNT(*) FROM events"); } } }
    public long PendingFrames { get { lock (gate) { return Number("SELECT COUNT(*) FROM frames WHERE done IN (0,3)"); } } }
    public long ReviewInputs { get { lock (gate) { return Number("SELECT COUNT(*) FROM events WHERE state='NeedsReview'"); } } }
    public long ReviewFrames { get { lock (gate) { return Number("SELECT COUNT(*) FROM frames WHERE done=2"); } } }
    public string DecoderState { get { lock (gate) { return Text("SELECT value FROM meta WHERE id='decoder'") ?? ""; } } }
    public long PendingUnits { get { lock (gate) { return Number("SELECT COALESCE(SUM(total-success-nochange-failed-unknown-cancelled),0) FROM jobs WHERE state NOT IN ('Cancelled','Succeeded','NoChange','Failed')"); } } }
    public string EngineState { get { lock (gate) { return Text("SELECT value FROM meta WHERE id='engine'") ?? ""; } } }

    /// <summary>配置版本独立保存，排队期间改规则不会更改已收礼物的含义。</summary>
    public string RegisterProfile(RuleProfile profile)
    {
        lock (gate) { var id = Guid.NewGuid().ToString("N"); Sql("INSERT INTO profiles VALUES($id,$json)", ("$id", id), ("$json", JsonConvert.SerializeObject(profile))); return id; }
    }

    /// <summary>帧先落盘，再由后台解码。</summary>
    public long AppendFrame(byte[] payload, string room, bool eligible, string profileId = "")
    {
        lock (gate)
        {
            Sql("INSERT INTO frames(payload,room,eligible,profile,at) VALUES($b,$r,$e,$p,$a)", ("$b", payload), ("$r", room), ("$e", eligible), ("$p", profileId), ("$a", DateTimeOffset.UtcNow.ToString("o")));
            return Number("SELECT last_insert_rowid()");
        }
    }
    public StoredFrame? ReadFrame()
    {
        lock (gate)
        {
            using var command = Command("SELECT id,payload,room,eligible,profile,at FROM frames WHERE done=0 ORDER BY id LIMIT 1"); using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetInt64(0), (byte[])reader[1], reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))) : null;
        }
    }
    public void CompleteFrame(long id) { lock (gate) { Sql("UPDATE frames SET done=1 WHERE id=$id", ("$id", id)); } }
    /// <summary>解码后的事件和帧完成状态一起提交，进程中断不会造成半帧漏账。</summary>
    public int CommitFrame(StoredFrame frame, IReadOnlyList<LiveEvent> values, string decoderState)
    {
        lock (gate)
        {
            var added = 0;
            Transaction(() =>
            {
                var eligible = ScalarDouble("SELECT eligible FROM frames WHERE id=$i", ("$i", frame.Id)) > 0;
                var stopped = Text("SELECT error FROM frames WHERE id=$i", ("$i", frame.Id)) ?? "";
                foreach (var value in values)
                {
                    value.ReceivedAt = frame.At;
                    added += Sql("INSERT OR IGNORE INTO events VALUES($id,$json,$e,$p,'Received',$reason,NULL,$at)", ("$id", Key(value)), ("$json", JsonConvert.SerializeObject(value)), ("$reason", stopped.StartsWith("STOPPED:", StringComparison.Ordinal) ? stopped : ""), ("$e", eligible), ("$p", frame.ProfileId), ("$at", frame.At.ToString("o")));
                }
                Sql("UPDATE frames SET done=1 WHERE id=$i", ("$i", frame.Id));
                Sql("INSERT OR REPLACE INTO meta VALUES('decoder',$s)", ("$s", decoderState));
            });
            return added;
        }
    }
    public void FailFrame(long id, string reason) { lock (gate) { Sql("UPDATE frames SET done=2,error=$r WHERE id=$i", ("$r", reason), ("$i", id)); } }
    public int RetryFailedFrames() { lock (gate) { return Sql("UPDATE frames SET done=0,error='' WHERE done=2"); } }
    public IReadOnlyList<RuntimeEntry> QueryInputs(LiveEventType? source = null, string search = "", int limit = 500)
    {
        lock (gate)
        {
            using var command = Command("SELECT json,state,reason FROM events WHERE ($s=-1 OR json_extract(json,'$.Type')=$s) AND ($q='' OR json LIKE $like OR reason LIKE $like) ORDER BY rowid DESC LIMIT $n", ("$s", source.HasValue ? (int)source.Value : -1), ("$q", search), ("$like", "%" + search + "%"), ("$n", limit));
            using var reader = command.ExecuteReader(); var list = new List<RuntimeEntry>();
            while (reader.Read())
            {
                var value = JsonConvert.DeserializeObject<LiveEvent>(reader.GetString(0))!; var state = reader.GetString(1); var reason = reader.GetString(2);
                list.Add(new(value.ReceivedAt.ToLocalTime(), state, value.Nickname + " · " + (value.Type == LiveEventType.Chat ? value.Text : value.Type == LiveEventType.Gift ? value.GiftName + " ×" + value.Count : value.Count.ToString()) + (reason.Length > 0 ? " · " + reason : ""), state != "NeedsReview", value, value.Nickname, value.Type));
            }
            return list;
        }
    }


    /// <summary>同一帧解出的多个事件一次提交；不使用有损 TryWrite。</summary>
    public int AdmitEvents(IEnumerable<LiveEvent> values, bool eligible, string profileId = "")
    {
        lock (gate)
        {
            var added = 0;
            Transaction(() =>
            {
                foreach (var value in values)
                {
                    added += Sql("INSERT OR IGNORE INTO events VALUES($id,$json,$e,$p,'Received','',NULL,$at)", ("$id", Key(value)), ("$json", JsonConvert.SerializeObject(value)), ("$e", eligible), ("$p", profileId), ("$at", value.ReceivedAt.ToString("o")));
                }
            });
            return added;
        }
    }
    public IReadOnlyList<StoredInput> ReadPendingEvents(int limit = 100)
    {
        lock (gate)
        {
            using var command = Command("SELECT json,eligible,profile FROM events WHERE state='Received' ORDER BY rowid LIMIT $n", ("$n", limit)); using var reader = command.ExecuteReader(); var rows = new List<StoredInput>();
            while (reader.Read()) { rows.Add(new(JsonConvert.DeserializeObject<LiveEvent>(reader.GetString(0))!, reader.GetBoolean(1), reader.GetString(2))); } return rows;
        }
    }

    /// <summary>一次事务处理一个事件，临时进程中断不会吞掉已入队单位。</summary>
    public bool ProcessNext(RuleEngine engine, RuleProfile fallback)
    {
        lock (gate)
        {
            var stored = ReadPendingEvents(1).FirstOrDefault(); if (stored == null) { return false; }
            var before = engine.SaveState();
            try
            {
                Transaction(() =>
                {
                    var raw = stored.Event;
                    if (raw.NeedsReview) { SetEvent(raw, "NeedsReview", raw.ReviewReason); return; }
                    var normalized = Normalize(raw);
                    if (normalized == null) { SetEvent(raw, "Duplicate", "重复的连击进度 / 结束包，没有重复触发"); return; }
                    if (!stored.Eligible)
                    {
                        var reason = Text("SELECT reason FROM events WHERE id=$i", ("$i", Key(raw))) ?? "";
                        SetEvent(raw, reason.StartsWith("STOPPED:", StringComparison.Ordinal) ? "Cancelled" : "Paused", reason.StartsWith("STOPPED:", StringComparison.Ordinal) ? reason.Substring(8) : "收到时互动开关关闭，未产生任务"); return;
                    }
                    var rules = stored.ProfileId.Length > 0 ? JsonConvert.DeserializeObject<RuleProfile>(Text("SELECT json FROM profiles WHERE id=$id", ("$id", stored.ProfileId)) ?? "null") ?? fallback : fallback;
                    var matches = engine.Evaluate(normalized, rules.Rules);
                    Decision(normalized, matches, matches.Count == 0 ? engine.ExplainNoMatch(normalized, rules.Rules) : "");
                    Sql("INSERT OR REPLACE INTO meta VALUES('engine',$s)", ("$s", engine.SaveState()));
                });
            }
            catch { engine.LoadState(before); throw; }
            return true;
        }
    }

    public RuntimeEntry InspectInput(LiveEvent input)
    {
        lock (gate)
        {
            using var command = Command("SELECT COALESCE(normalized,json),state,reason FROM events WHERE id=$id", ("$id", Key(input))); using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new InvalidDataException("已接收事件不在账本中。"); }
            var value = JsonConvert.DeserializeObject<LiveEvent>(reader.GetString(0))!; var state = reader.GetString(1); var reason = reader.GetString(2);
            var detail = value.Nickname + " · " + (value.Type == LiveEventType.Chat ? value.Text : value.Type == LiveEventType.Gift ? value.GiftName + " ×" + value.Count : value.Count.ToString()) + (reason.Length > 0 ? " · " + reason : "");
            return new(value.ReceivedAt.ToLocalTime(), state, detail, state != "NeedsReview" && !(state == "Unmatched" && value.Type == LiveEventType.Gift), value, value.Nickname, value.Type);
        }
    }

    /// <summary>测试和明确构造端使用的原子入队。</summary>
    public void CommitDecision(LiveEvent input, IReadOnlyList<RuleMatch> matches, string reason)
    {
        lock (gate) { Transaction(() => Decision(input, matches, reason)); }
    }
    private void Decision(LiveEvent input, IReadOnlyList<RuleMatch> matches, string reason)
    {
        if (Text("SELECT state FROM events WHERE id=$id", ("$id", Key(input))) != "Received") { return; }
        foreach (var match in matches)
        {
            var rule = match.Rule;
            var job = new InteractionJob { Id = Guid.NewGuid().ToString("N"), EventId = Key(input), RuleId = rule.Id, RuleName = rule.Name, Triggerer = input.Nickname, UserId = input.UserId, SourceType = input.Type, At = input.ReceivedAt, Root = rule.Root, Total = match.Count, IsSimulation = input.IsSimulation, CooldownSeconds = rule.CooldownSeconds, UserCooldownSeconds = rule.UserCooldownSeconds, Context = new() { EventCount = input.Count, ComboTotal = input.ComboTotal, InputText = input.Text } };
            Sql("INSERT INTO jobs(id,event_id,rule_id,who,source,json,state,reason,total,simulation) VALUES($i,$e,$r,$w,$s,$j,'Queued','',$n,$sim)", ("$i", job.Id), ("$e", job.EventId), ("$r", job.RuleId), ("$w", job.Triggerer), ("$s", (int)job.SourceType), ("$j", JsonConvert.SerializeObject(job)), ("$n", job.Total), ("$sim", job.IsSimulation));
        }
        Sql("UPDATE events SET state=$s,reason=$r,normalized=$j WHERE id=$id", ("$s", matches.Count > 0 ? "Queued" : "Unmatched"), ("$r", reason), ("$j", JsonConvert.SerializeObject(input)), ("$id", Key(input)));
    }

    /// <summary>礼物优先，随后按接收顺序；等待中的任务不挡住其他可执行任务。</summary>
    public InteractionJob? AcquireNext(bool simulationOnly = false)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            using var command = Command("SELECT json,state,reason,total,success,nochange,failed,unknown,cancelled FROM jobs WHERE state IN ('Queued','Waiting') AND next <= $t AND ($sim=0 OR simulation=1) ORDER BY CASE WHEN source=1 THEN 0 ELSE 1 END,seq LIMIT 64", ("$t", now), ("$sim", simulationOnly));
            using var reader = command.ExecuteReader(); var rows = new List<InteractionJob>(); while (reader.Read()) { rows.Add(Job(reader)); } reader.Close();
            foreach (var job in rows)
            {
                var lastRule = ScalarDouble("SELECT at FROM throttles WHERE id=$id", ("$id", job.RuleId));
                var lastUser = ScalarDouble("SELECT at FROM throttles WHERE id=$id", ("$id", job.RuleId + ":" + job.UserId));
                var next = Math.Max(lastRule + job.CooldownSeconds, lastUser + job.UserCooldownSeconds);
                if (next > now) { Defer(job.Id, "按主播设置的冷却间隔排队", next - now); continue; }
                Sql("UPDATE jobs SET state='Preparing' WHERE id=$i", ("$i", job.Id)); job.State = "Preparing"; return job;
            }
            return null;
        }
    }
    public void Defer(string id, string reason, double seconds = 0.4)
    {
        lock (gate) { Sql("UPDATE jobs SET state='Waiting',reason=$r,next=$t WHERE id=$i", ("$r", reason), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d + seconds), ("$i", id)); }
    }
    public void RecordIntent(ActionRequest request)
    {
        if (request.JobId.Length == 0) { return; }
        lock (gate)
        {
            if (Text("SELECT state FROM jobs WHERE id=$i", ("$i", request.JobId)) is "Cancelled" or "Stopping" or "UnknownStopped") { throw new OperationCanceledException("主播已经取消此任务，不再发送。"); }
            Transaction(() =>
            {
                Sql("INSERT OR REPLACE INTO commands VALUES($i,$j,$u,$r,NULL,'Running')", ("$i", request.Id), ("$j", request.JobId), ("$u", request.UnitIndex), ("$r", JsonConvert.SerializeObject(request)));
                Sql("UPDATE jobs SET state='Running' WHERE id=$i", ("$i", request.JobId));
            });
        }
    }
    public void RecordResult(ActionRequest request, ActionResult result)
    {
        if (request.JobId.Length == 0) { return; }
        lock (gate) { Sql("INSERT OR REPLACE INTO commands VALUES($i,$j,$u,$q,$r,$s)", ("$i", request.Id), ("$j", request.JobId), ("$u", request.UnitIndex), ("$q", JsonConvert.SerializeObject(request)), ("$r", JsonConvert.SerializeObject(result)), ("$s", result.Success ? "Succeeded" : result.ErrorCode == "UNKNOWN" ? "Unknown" : "Failed")); }
    }
    public ActionResult? LastResult(string jobId, long unit)
    {
        lock (gate) { var json = Text("SELECT result FROM commands WHERE job=$j AND unit=$u AND result IS NOT NULL ORDER BY rowid DESC LIMIT 1", ("$j", jobId), ("$u", unit)); return json == null ? null : JsonConvert.DeserializeObject<ActionResult>(json); }
    }

    /// <summary>暂不可执行不消费单位；未确认结果单独保留，绝不自动重放。</summary>
    public void CompleteUnit(string id, ActionResult result)
    {
        lock (gate)
        {
            var job = QueryJobs(null, "", "", id).Single();
            if (job.State == "Cancelled") { return; }
            var stopped = job.State is "Stopping" or "UnknownStopped";
            if (result.Retryable)
            {
                if (stopped) { Sql("UPDATE jobs SET cancelled=cancelled+1,state='Cancelled',reason=$r WHERE id=$i", ("$r", "主播已停止；在途单位确认尚未执行，已取消"), ("$i", id)); }
                else { Defer(id, result.Message); }
                return;
            }
            Transaction(() =>
            {
                var unknown = result.ErrorCode == "UNKNOWN" || result.Message.Contains("结果未知", StringComparison.Ordinal);
                if (result.Success && result.ErrorCode == "NO_CHANGE") { job.NoChange++; }
                else if (result.Success) { job.Succeeded++; }
                else if (unknown) { job.Unknown++; }
                else { job.Failed++; }
                job.State = unknown ? stopped ? "UnknownStopped" : "Unknown" : stopped || job.Cancelled > 0 ? "Cancelled" : job.Remaining > 0 ? "Queued" : job.Failed > 0 ? "Failed" : job.NoChange == job.Total ? "NoChange" : "Succeeded";
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
                Sql("UPDATE jobs SET state=$s,reason=$r,success=$ok,nochange=$nc,failed=$f,unknown=$u,next=0 WHERE id=$i", ("$s", job.State), ("$r", result.Message), ("$ok", job.Succeeded), ("$nc", job.NoChange), ("$f", job.Failed), ("$u", job.Unknown), ("$i", id));
                Sql("INSERT OR REPLACE INTO throttles VALUES($i,$t)", ("$i", job.RuleId), ("$t", now));
                Sql("INSERT OR REPLACE INTO throttles VALUES($i,$t)", ("$i", job.RuleId + ":" + job.UserId), ("$t", now));
            });
        }
    }

    /// <summary>界面筛选在账本查询层执行，不用为了显示筛选而丢弃消息。</summary>
    public IReadOnlyList<InteractionJob> QueryJobs(LiveEventType? source = null, string state = "", string search = "", string id = "", int limit = 500)
    {
        lock (gate)
        {
            using var command = Command("SELECT json,state,reason,total,success,nochange,failed,unknown,cancelled FROM jobs WHERE ($source=-1 OR source=$source) AND ($state='' OR state=$state OR ($state='Failed' AND failed>0) OR ($state='Unknown' AND unknown>0) OR ($state='Running' AND state IN ('Preparing','Stopping')) OR ($state='Cancelled' AND cancelled>0)) AND ($q='' OR who LIKE $like OR reason LIKE $like OR json LIKE $like) AND ($id='' OR id=$id) ORDER BY seq DESC LIMIT $n", ("$source", source.HasValue ? (int)source.Value : -1), ("$state", state), ("$q", search), ("$like", "%" + search + "%"), ("$id", id), ("$n", limit));
            using var reader = command.ExecuteReader(); var rows = new List<InteractionJob>(); while (reader.Read()) { rows.Add(Job(reader)); } return rows;
        }
    }
    /// <summary>找到最后一个发出但没有确定结果的指令，只查询回执，不重新执行。</summary>
    public ActionRequest? UnknownCommand(string jobId)
    {
        lock (gate)
        {
            var json = Text("SELECT request FROM commands WHERE job=$j AND state IN ('Running','Unknown') ORDER BY rowid DESC LIMIT 1", ("$j", jobId));
            return json == null ? null : JsonConvert.DeserializeObject<ActionRequest>(json);
        }
    }
    public void ResolveUnknown(string jobId, ActionResult known)
    {
        lock (gate)
        {
            var job = QueryJobs(null, "", "", jobId).Single();
            if (job.Unknown == 0 || job.Root.Kind != StepKind.Action) { return; }
            var success = known.Success && known.ErrorCode != "NO_CHANGE" ? 1 : 0;
            var nochange = known.Success && known.ErrorCode == "NO_CHANGE" ? 1 : 0;
            var failure = !known.Success && !known.Retryable ? 1 : 0;
            var stopped = job.State == "UnknownStopped";
            var cancelled = stopped && known.Retryable ? 1 : 0;
            var nextRemaining = job.Remaining + (known.Retryable && !stopped ? 1 : 0);
            var state = stopped || job.Cancelled > 0 ? "Cancelled" : nextRemaining > 0 ? "Queued" : job.Failed + failure > 0 ? "Failed" : "Succeeded";
            Sql("UPDATE jobs SET unknown=unknown-1,success=success+$ok,nochange=nochange+$nc,failed=failed+$f,cancelled=cancelled+$c,state=$s,reason=$r,next=0 WHERE id=$id", ("$ok", success), ("$nc", nochange), ("$f", failure), ("$c", cancelled), ("$s", state), ("$r", "游戏回执核对：" + known.Message), ("$id", jobId));
        }
    }

    public void RecoverAfterRestart()
    {
        lock (gate)
        {
            Sql("UPDATE frames SET done=3 WHERE done=0 AND eligible=1; UPDATE jobs SET unknown=unknown+1,state='Unknown',reason='上次退出时已发出，结果待核对；未自动重放' WHERE state='Running'; UPDATE jobs SET unknown=unknown+1,state='UnknownStopped',reason='上次停止时在途结果待核对，余量已取消' WHERE state='Stopping'; UPDATE jobs SET state='AwaitingResume',reason='上次退出前未完成，等待主播恢复' WHERE state IN ('Queued','Preparing','Waiting');");
        }
    }
    public int ResumePending() { lock (gate) { return Sql("UPDATE jobs SET state='Queued',reason='主播已恢复未完成队列',next=0 WHERE state='AwaitingResume'") + Sql("UPDATE frames SET done=0 WHERE done=3"); } }
    public void CancelPending(string reason)
    {
        lock (gate)
        {
            Transaction(() =>
            {
                Sql("UPDATE events SET eligible=0,reason=$r WHERE state='Received'; UPDATE frames SET eligible=0,error=$r WHERE done IN (0,2,3)", ("$r", "STOPPED:" + reason));
                Sql("UPDATE jobs SET cancelled=total-success-nochange-failed-unknown,state='Cancelled',reason=$r WHERE state IN ('Queued','Waiting','Preparing','AwaitingResume')", ("$r", reason));
                Sql("UPDATE jobs SET cancelled=MAX(0,total-success-nochange-failed-unknown-1),state='Stopping',reason=$r WHERE state='Running'", ("$r", reason));
                Sql("UPDATE jobs SET cancelled=total-success-nochange-failed-unknown,state='UnknownStopped',reason=$r WHERE state='Unknown'", ("$r", reason + "；在途结果仍需核对"));
            });
        }
    }

    /// <summary>仅清理已成功解码的旧帧，未处理内容不因数量上限删除。</summary>
    public void PruneDecodedFrames()
    {
        lock (gate) { Sql("DELETE FROM frames WHERE done=1 AND at<$at", ("$at", DateTimeOffset.UtcNow.AddDays(-7).ToString("o"))); }
    }
    public void Dispose() { lock (gate) { database.Dispose(); } }

    private LiveEvent? Normalize(LiveEvent raw)
    {
        var value = JsonConvert.DeserializeObject<LiveEvent>(JsonConvert.SerializeObject(raw))!;
        if (value.Type != LiveEventType.Gift || !value.IsCumulative || value.ComboId.Length == 0) { value.Count = Math.Max(0, value.Count); value.ComboTotal = Math.Max(value.ComboTotal, value.Count); return value; }
        var key = value.RoomId + ":" + value.UserId + ":" + value.GiftId + ":" + value.ComboId;
        long previous = 0; var ended = false;
        using (var command = Command("SELECT quantity,ended FROM combos WHERE id=$i", ("$i", key)))
        using (var reader = command.ExecuteReader()) { if (reader.Read()) { previous = reader.GetInt64(0); ended = reader.GetBoolean(1); } }
        var total = Math.Max(previous, Math.Max(0, value.Count)); value.ComboEnded = value.ComboEnded && !ended; value.Count = total - previous; value.ComboTotal = total;
        Sql("INSERT OR REPLACE INTO combos VALUES($i,$q,$e,$a)", ("$i", key), ("$q", total), ("$e", ended || value.ComboEnded), ("$a", value.ReceivedAt.ToString("o")));
        return value.Count == 0 && !value.ComboEnded ? null : value;
    }
    private static string Key(LiveEvent value) => value.RoomId + ":" + value.Id;
    private void SetEvent(LiveEvent value, string state, string reason) => Sql("UPDATE events SET state=$s,reason=$r WHERE id=$i", ("$s", state), ("$r", reason), ("$i", Key(value)));
    private static InteractionJob Job(SqliteDataReader reader)
    {
        var job = JsonConvert.DeserializeObject<InteractionJob>(reader.GetString(0))!; job.State = reader.GetString(1); job.Reason = reader.GetString(2); job.Total = reader.GetInt64(3); job.Succeeded = reader.GetInt64(4); job.NoChange = reader.GetInt64(5); job.Failed = reader.GetInt64(6); job.Unknown = reader.GetInt64(7); job.Cancelled = reader.GetInt64(8); return job;
    }
    private SqliteCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = database.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var pair in parameters) { command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value); } return command;
    }
    private int Sql(string sql, params (string Key, object? Value)[] parameters) { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); }
    private long Number(string sql, params (string Key, object? Value)[] parameters) { using var command = Command(sql, parameters); return Convert.ToInt64(command.ExecuteScalar() ?? 0); }
    private string? Text(string sql, params (string Key, object? Value)[] parameters) { using var command = Command(sql, parameters); return command.ExecuteScalar() as string; }
    private double ScalarDouble(string sql, params (string Key, object? Value)[] parameters) { using var command = Command(sql, parameters); return Convert.ToDouble(command.ExecuteScalar() ?? 0); }
    private void Transaction(Action action)
    {
        using var current = database.BeginTransaction(); transaction = current;
        try { action(); current.Commit(); } finally { transaction = null; }
    }
}
