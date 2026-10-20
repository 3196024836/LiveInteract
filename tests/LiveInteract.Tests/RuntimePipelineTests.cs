using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>完整本地运行管道的构造端验收，不冒充真实抖音或真实游戏战斗。</summary>
public sealed class RuntimePipelineTests
{
    [Fact]
    public async Task FiveThousandUnitsHaveOneReceiptEachDespiteDuplicateInputs()
    {
        var adapter = new Adapter(); await using var runtime = new LiveRuntime(false, testAdapter: adapter);
        runtime.ApplyProfile(Profile()); runtime.SetEnabled(true);
        for (var n = 0; n < 50; n++)
        {
            var value = new LiveEvent { Id = "group" + n, Type = LiveEventType.Gift, GiftName = "小心心", Nickname = "观众" + n, Count = 100 };
            runtime.Submit(value); runtime.Submit(value);
        }
        await Wait(() => runtime.Completed == 5000);
        Assert.Equal(50, runtime.Received); Assert.Equal(5000, adapter.Applied); Assert.Equal(5000, adapter.Ids.Count); Assert.Equal(0, runtime.Pending); Assert.Equal(0, runtime.Failed);
        Assert.Equal(5000, runtime.Journal.QueryJobs().Sum(job => job.Succeeded));
    }
    [Fact]
    public async Task BusyBeforeApplyRetriesSameUnitWithoutConsumingIt()
    {
        var adapter = new Adapter { BusyOnce = true }; await using var runtime = new LiveRuntime(false, testAdapter: adapter);
        runtime.ApplyProfile(Profile()); runtime.SetEnabled(true);
        runtime.Submit(new() { Id = "busy", Type = LiveEventType.Gift, Count = 3 });
        await Wait(() => runtime.Completed == 3);
        Assert.Equal(3, adapter.Applied); Assert.Equal(4, adapter.Calls); Assert.Equal(3, adapter.Ids.Count); Assert.Equal(0, runtime.Failed);
    }
    [Fact]
    public async Task UnknownNeverAutomaticallyReplaysOrCountsAsSuccess()
    {
        var adapter = new Adapter { Unknown = true }; await using var runtime = new LiveRuntime(false, testAdapter: adapter);
        runtime.ApplyProfile(Profile()); runtime.SetEnabled(true); runtime.Submit(new() { Id = "unknown", Type = LiveEventType.Gift, Count = 3 });
        await Wait(() => runtime.Journal.QueryJobs().Any(job => job.State == "Unknown")); await Task.Delay(600);
        var job = Assert.Single(runtime.Journal.QueryJobs()); Assert.Equal(1, adapter.Calls); Assert.Equal(0, runtime.Completed); Assert.Equal(1, job.Unknown); Assert.Equal(2, job.Remaining);
    }
    [Fact]
    public async Task RestartPreservesUnsentJobsUntilExplicitResume()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LiveInteract-Restart", Guid.NewGuid().ToString("N"));
        var offline = new Adapter { Online = false };
        await using (var first = new LiveRuntime(false, folder, offline))
        {
            first.ApplyProfile(Profile()); first.SetEnabled(true); first.Submit(new() { Id = "keep", Type = LiveEventType.Gift, Count = 30 });
            await Wait(() => first.Pending == 30);
        }
        var online = new Adapter(); await using var second = new LiveRuntime(false, folder, online);
        second.SetEnabled(true); await Task.Delay(200); Assert.Equal(0, online.Applied);
        Assert.Equal("AwaitingResume", Assert.Single(second.Journal.QueryJobs()).State);
        Assert.Equal(1, second.ResumePending()); await Wait(() => second.Completed == 30); Assert.Equal(30, online.Applied);
    }
    private static RuleProfile Profile() => new() { Rules = [new() { Name = "测试礼物", Enabled = true, Trigger = new() { Type = LiveEventType.Gift }, Root = new() { ActionId = "health.add", Arguments = new() { ["value"] = "1" } } }] };
    private static async Task Wait(Func<bool> predicate)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(30); while (!predicate() && DateTimeOffset.UtcNow < end) { await Task.Delay(10); } Assert.True(predicate(), "管道未在 30 秒内达到预期状态");
    }
    private sealed class Adapter : IGameAdapter
    {
        public bool Online { get; set; } = true;
        public bool BusyOnce { get; set; }
        public bool Unknown { get; set; }
        public int Applied, Calls;
        public HashSet<string> Ids { get; } = [];
        public bool IsConnected => Online;
        public GameStatus Status => new() { CanAct = true };
        public event Action<GameStatus>? StatusChanged { add { } remove { } }
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken token)
        {
            Calls++;
            if (BusyOnce && Calls == 1) { return Task.FromResult(new ActionResult { RequestId = request.Id, ErrorCode = "BUSY", Retryable = true, Message = "角色忙碌，尚未应用" }); }
            if (Unknown) { return Task.FromResult(new ActionResult { RequestId = request.Id, ErrorCode = "UNKNOWN", Message = "结果未知" }); }
            if (Ids.Add(request.Id)) { Applied++; }
            return Task.FromResult(new ActionResult { RequestId = request.Id, Success = true, Message = "构造端回执" });
        }
        public Task ResetAsync(CancellationToken token) => Task.CompletedTask;
    }
}
