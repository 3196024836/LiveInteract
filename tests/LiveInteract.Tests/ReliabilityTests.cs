using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>以超过旧队列容量的数据量验证持久入站、完整礼物数量与可核对结果。</summary>
public sealed class ReliabilityTests
{
    /// <summary>新可靠模式保留礼物数量，冷却只决定排队间隔而不吞单位。</summary>
    [Fact]
    public void GiftsAreNotTruncatedByOldBatchLimitOrCooldown()
    {
        var engine = new RuleEngine(reliable: true);
        var rule = new RuleDefinition { Name = "heart", Enabled = true, MaximumPerEvent = 20, CooldownSeconds = 2, Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心" } };
        Assert.Equal(800, Assert.Single(engine.Evaluate(new() { Id = "gift800", Type = LiveEventType.Gift, GiftName = "小心心", Count = 800 }, [rule])).Count);
        Assert.Equal(3, Assert.Single(engine.Evaluate(new() { Id = "gift3", Type = LiveEventType.Gift, GiftName = "小心心", Count = 3 }, [rule])).Count);
    }

    /// <summary>收到的事件先落盘；超过旧输入容量不丢失，重放不重复入账。</summary>
    [Fact]
    public void ReceivedEventsSurviveRestartAndDeduplicate()
    {
        var file = Temp();
        var values = Enumerable.Range(0, 5000).Select(n => new LiveEvent { Id = "e" + n, Type = n % 2 == 0 ? LiveEventType.Gift : LiveEventType.Chat, Nickname = "测试观众", GiftName = "小心心" }).ToList();
        using (var journal = new InteractionJournal(file))
        {
            Assert.Equal(5000, journal.AdmitEvents(values, true));
            Assert.Equal(0, journal.AdmitEvents(values, true));
            Assert.Equal(5000, journal.ReceivedCount);
        }
        using var reopened = new InteractionJournal(file);
        Assert.Equal(5000, reopened.ReceivedCount);
        Assert.Equal(5000, reopened.ReadPendingEvents(6000).Count);
    }

    /// <summary>大量礼物以带剩余数量的任务保存，不受旧 256 动作队列限制。</summary>
    [Fact]
    public void QueueKeepsAllUnitsAndUnknownIsNotSuccess()
    {
        using var journal = new InteractionJournal(Temp());
        var input = new LiveEvent { Id = "g", Type = LiveEventType.Gift, Nickname = "观众甲", GiftName = "小心心", Count = 5000 };
        journal.AdmitEvents([input], true);
        var rule = new RuleDefinition { Name = "礼物助力", Root = new() { ActionId = "health.add" } };
        journal.CommitDecision(input, [new RuleMatch(rule, 5000)], "");
        var job = Assert.Single(journal.QueryJobs()); Assert.Equal(5000, job.Total);
        journal.CompleteUnit(job.Id, new ActionResult { Success = true, Message = "applied" });
        job = Assert.Single(journal.QueryJobs()); Assert.Equal(1, job.Succeeded); Assert.Equal(4999, job.Remaining);
        journal.CompleteUnit(job.Id, new ActionResult { Success = false, ErrorCode = "UNKNOWN", Message = "已发出，结果未知" });
        job = Assert.Single(journal.QueryJobs()); Assert.Equal(1, job.Succeeded); Assert.Equal(1, job.Unknown); Assert.Equal("Unknown", job.State); Assert.Equal(4998, job.Remaining);
    }

    /// <summary>角色暂不可操作只等待，不消费礼物单位，也不算永久失败。</summary>
    [Fact]
    public void DeferredUnitKeepsQuantityAndSourceCanBeFiltered()
    {
        using var journal = new InteractionJournal(Temp());
        foreach (var kind in new[] { LiveEventType.Gift, LiveEventType.Chat })
        {
            var input = new LiveEvent { Id = kind.ToString(), Type = kind, Nickname = kind == LiveEventType.Gift ? "观众甲" : "观众乙" };
            journal.AdmitEvents([input], true); journal.CommitDecision(input, [new RuleMatch(new() { Name = "助力" }, 5)], "");
        }
        var gift = Assert.Single(journal.QueryJobs(LiveEventType.Gift, "", "观众甲"));
        journal.Defer(gift.Id, "等待进入角色场景");
        var waiting = Assert.Single(journal.QueryJobs(LiveEventType.Gift, "Waiting", ""));
        Assert.Equal(5, waiting.Remaining); Assert.Equal(0, waiting.Failed); Assert.Contains("等待", waiting.Reason);
        Assert.Single(journal.QueryJobs(LiveEventType.Chat));
    }

    /// <summary>浏览器帧不会在 256 条后溢出；解码前可以恢复。</summary>
    [Fact]
    public void FramesAreDurableAndCompleteOnlyAfterDecode()
    {
        using var journal = new InteractionJournal(Temp());
        for (var n = 0; n < 600; n++) { journal.AppendFrame([1, 2, 3], "fixture", true); }
        Assert.Equal(600, journal.PendingFrames);
        var frame = journal.ReadFrame()!; Assert.Equal(new byte[] { 1, 2, 3 }, frame.Payload);
        journal.CompleteFrame(frame.Id);
        Assert.Equal(599, journal.PendingFrames);
    }

    /// <summary>没有游戏时整批输入仍保留，不能因为队列满或角色未就绪消耗礼物。</summary>
    [Fact]
    public async Task RuntimeRetainsBurstWhileGameIsDisconnected()
    {
        await using var runtime = new LiveRuntime(false);
        runtime.ApplyProfile(new() { Rules = [new() { Name = "礼物", Enabled = true, Trigger = new() { Type = LiveEventType.Gift }, Root = new() { ActionId = "health.add" } }] });
        runtime.SetEnabled(true);
        for (var n = 0; n < 5000; n++) { runtime.Submit(new() { Id = "burst" + n, Type = LiveEventType.Gift, Count = 1 }); }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((runtime.Received < 5000 || runtime.Pending < 5000) && DateTimeOffset.UtcNow < deadline) { await Task.Delay(20); }
        Assert.Equal(5000, runtime.Received); Assert.Equal(5000, runtime.Pending); Assert.Equal(0, runtime.Failed);
    }

    /// <summary>每个测试使用独立本机目录。</summary>
    private static string Temp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LiveInteract-Reliability", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return Path.Combine(directory, "events.db");
    }
}
