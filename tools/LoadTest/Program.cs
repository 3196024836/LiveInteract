using System.Diagnostics;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json;

/// <summary>以指定真实时长和速率回放构造事件；不连接平台账号或真正游戏。</summary>
internal static class Program
{
    /// <summary>运行有界会话负载并写入独立证据。</summary>
    /// <param name="args">秒数与证据路径。</param>
    /// <returns>成功返回零，出现异常返回一。</returns>
    private static async Task<int> Main(string[] args)
    {
        var seconds = args.Length > 0 ? int.Parse(args[0]) : 600; // 默认十分钟。
        var output = args.Length > 1 ? args[1] : "load-test.json"; // 结果文件。
        var normalizer = new EventNormalizer(); var engine = new RuleEngine(); var adapter = new CountingAdapter(); var executor = new ActionExecutor(adapter); // 隔离的完整核心链路。
        var rules = new List<RuleDefinition>
        {
            new() { Enabled = true, Trigger = new() { Type = LiveEventType.Chat, Keywords = ["test"] }, Root = new() { ActionId = "health.full" } },
            new() { Enabled = true, Trigger = new() { Type = LiveEventType.Like, Counting = TriggerCounting.RoomTotal, Threshold = 10 }, Root = new() { ActionId = "silk.full" } },
            new() { Enabled = true, Trigger = new() { Type = LiveEventType.Gift }, Root = new() { ActionId = "health.full" } },
            new() { Enabled = true, Trigger = new() { Type = LiveEventType.Gift, Counting = TriggerCounting.ComboEnd, SingleExecution = true }, Root = new() { ActionId = "game.status" } }
        };
        var watch = Stopwatch.StartNew(); var before = GC.GetTotalMemory(true); long count = 0, normalized = 0, matches = 0; // 可检查计量。
        Console.WriteLine("Offline load test: 100 events/second, " + seconds + " seconds; no platform or game connection.");
        try
        {
            for (var batch = 0; batch < seconds * 10; batch++)
            {
                for (var item = 0; item < 10; item++)
                {
                    var index = count++; var kind = index % 5; var group = index / 5; // 五条为一组混合输入。
                    var raw = new LiveEvent
                    {
                        Id = "fixture:" + index, RoomId = "fixture-room", UserId = "fixture-user",
                        Type = kind == 0 ? LiveEventType.Chat : kind == 1 ? LiveEventType.Like : LiveEventType.Gift, Text = "test",
                        Count = kind == 1 ? 3 : kind >= 3 ? 3 : 1, GiftId = "fixture-gift", ComboId = group.ToString(),
                        IsCumulative = kind >= 2, ComboEnded = kind == 4, IsSimulation = true, ReceivedAt = DateTimeOffset.UtcNow
                    };
                    var value = normalizer.Normalize(raw); if (value == null) { continue; } normalized++;
                    foreach (var match in engine.Evaluate(value, rules))
                    {
                        matches += match.Count;
                        for (var repeat = 0; repeat < match.Count; repeat++)
                        {
                            if (!await executor.ExecuteAsync(match.Rule.Root, "fixture", false, default, new() { EventCount = value.Count, ComboTotal = value.ComboTotal, TriggerIndex = repeat + 1 })) { throw new InvalidOperationException("构造端执行失败。"); }
                        }
                    }
                }
                var remaining = TimeSpan.FromMilliseconds((batch + 1) * 100) - watch.Elapsed; if (remaining > TimeSpan.Zero) { await Task.Delay(remaining); }
            }
            File.WriteAllText(output, JsonConvert.SerializeObject(new { Passed = true, Verification = "离线构造事件，不证明真实抖音可用", RequestedSeconds = seconds, ActualSeconds = watch.Elapsed.TotalSeconds, Events = count, Normalized = normalized, RuleMatches = matches, Commands = adapter.Count, EventsPerSecond = count / watch.Elapsed.TotalSeconds, ManagedMemoryBefore = before, ManagedMemoryAfter = GC.GetTotalMemory(true), PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64, ActualGameModified = false }, Formatting.Indented));
            Console.WriteLine("PASS: " + count + " events, " + adapter.Count + " acknowledged fixture commands."); return 0;
        }
        catch (Exception exception) { File.WriteAllText(output, JsonConvert.SerializeObject(new { Passed = false, Events = count, Error = exception.ToString() }, Formatting.Indented)); Console.Error.WriteLine(exception); return 1; }
    }

    /// <summary>只计算调用数量，绝不伪装实际游戏。</summary>
    private sealed class CountingAdapter : IGameAdapter
    {
        /// <summary>构造连接状态。</summary>
        public bool IsConnected => true;
        /// <summary>无游戏状态。</summary>
        public GameStatus? Status => null;
        /// <summary>无实际心跳。</summary>
        public event Action<GameStatus>? StatusChanged { add { } remove { } }
        /// <summary>完成的构造指令数。</summary>
        public long Count { get; private set; }

        /// <summary>返回明确的构造回执。</summary>
        /// <param name="request">待测请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>构造成功回执。</returns>
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            Count++; return Task.FromResult(new ActionResult { RequestId = request.Id, Success = true, Message = "offline fixture" });
        }

        /// <summary>构造端没有游戏状态需要恢复。</summary>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>完成任务。</returns>
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
