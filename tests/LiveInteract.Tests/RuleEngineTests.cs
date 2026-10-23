using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>验证关键规则语义，不连接账号或启动游戏。</summary>
public sealed class RuleEngineTests
{
    /// <summary>精确匹配能触发，重放同一事件不能触发两次。</summary>
    [Fact]
    public void ExactMatchAndDeduplication()
    {
        var engine = new RuleEngine(); // 独立会话。
        var rule = ChatRule(); // 启用的精确匹配规则。
        var input = Chat("one", "回血"); // 真实语义的构造输入。
        Assert.Single(engine.Evaluate(input, [rule]));
        Assert.Empty(engine.Evaluate(input, [rule]));
        Assert.Empty(engine.Evaluate(Chat("two", "帮我回血"), [rule]));
    }

    /// <summary>累计点赞跨越门槛时保留余数。</summary>
    [Fact]
    public void LikeThresholdRetainsRemainder()
    {
        var engine = new RuleEngine(); // 独立累计状态。
        var rule = ChatRule(); // 可修改的测试规则。
        rule.Trigger = new() { Type = LiveEventType.Like, Threshold = 10, Counting = TriggerCounting.RoomTotal };
        Assert.Empty(engine.Evaluate(new() { Id = "l1", Type = LiveEventType.Like, Count = 7 }, [rule]));
        Assert.Equal(2, Assert.Single(engine.Evaluate(new() { Id = "l2", Type = LiveEventType.Like, Count = 15 }, [rule])).Count);
        Assert.Single(engine.Evaluate(new() { Id = "l3", Type = LiveEventType.Like, Count = 8 }, [rule]));
    }

    /// <summary>缺失身份信息不能满足粉丝或关注条件。</summary>
    [Fact]
    public void UnknownIdentityFailsClosed()
    {
        var rule = ChatRule(); // 需要已知身份的规则。
        rule.Trigger.MinimumFanLevel = 3;
        rule.Trigger.RequireFollower = true;
        Assert.Empty(new RuleEngine().Evaluate(Chat("u1", "回血"), [rule]));
        var input = Chat("u2", "回血"); // 补齐被源提供的身份。
        input.FanLevel = 3;
        input.IsFollower = true;
        Assert.Single(new RuleEngine().Evaluate(input, [rule]));
    }

    /// <summary>冷却不依赖实际等待；到期后允许再次触发。</summary>
    [Fact]
    public void CooldownUsesInjectedClock()
    {
        var now = DateTimeOffset.UtcNow; // 可控时钟。
        var engine = new RuleEngine(() => now); // 注入会话时间。
        var rule = ChatRule(); // 十秒冷却。
        rule.CooldownSeconds = 10;
        Assert.Single(engine.Evaluate(Chat("a", "回血"), [rule]));
        Assert.Empty(engine.Evaluate(Chat("b", "回血"), [rule]));
        now = now.AddSeconds(10);
        Assert.Single(engine.Evaluate(Chat("c", "回血"), [rule]));
    }

    /// <summary>概率零和禁用规则不能执行。</summary>
    [Fact]
    public void DisabledAndProbabilityZeroNeverFire()
    {
        var rule = ChatRule(); // 用于切换状态。
        rule.Probability = 0;
        Assert.Empty(new RuleEngine(random: () => 0).Evaluate(Chat("p1", "回血"), [rule]));
        rule.Probability = 1;
        rule.Enabled = false;
        Assert.Empty(new RuleEngine().Evaluate(Chat("p2", "回血"), [rule]));
    }

    /// <summary>非空白名单不允许未知身份绕过。</summary>
    [Fact]
    public void UserAllowListRejectsAnonymous()
    {
        var rule = ChatRule(); // 只接受指定用户。
        rule.Trigger.AllowedUsers.Add("42");
        Assert.Empty(new RuleEngine().Evaluate(Chat("w1", "回血"), [rule]));
        var input = Chat("w2", "回血"); // 指定允许身份。
        input.UserId = "42";
        Assert.Single(new RuleEngine().Evaluate(input, [rule]));
    }

    /// <summary>一个事件可以命中多条，停止匹配选项只作用于之后的规则。</summary>
    [Fact]
    public void RuleOrderingAndStopAfterMatch()
    {
        var first = ChatRule(); // 前置规则。
        var second = ChatRule(); // 后置规则。
        Assert.Equal(2, new RuleEngine().Evaluate(Chat("m1", "回血"), [first, second]).Count);
        first.StopAfterMatch = true;
        Assert.Single(new RuleEngine().Evaluate(Chat("m2", "回血"), [first, second]));
    }

    /// <summary>可靠模式不记录用不到的冷却，观众再多状态也不随人数增长。</summary>
    [Fact]
    public void ReliableModeStateDoesNotGrowWithViewers()
    {
        var engine = new RuleEngine(reliable: true); // 运行时使用的可靠模式。
        var rule = ChatRule(); // 每条弹幕都命中的规则。
        for (var index = 0; index < 2000; index++) { engine.Evaluate(new() { Id = "c" + index, Type = LiveEventType.Chat, Text = "回血", UserId = "u" + index }, [rule]); }
        Assert.True(engine.SaveState().Length < 200, engine.SaveState().Length.ToString());
    }

    /// <summary>已结算的单观众计数会删除，超过上限也不会让输入流水线抛错停摆。</summary>
    [Fact]
    public void PerUserCountersAreBoundedWithoutThrowing()
    {
        var engine = new RuleEngine(reliable: true); // 独立累计状态。
        var settled = ChatRule(); // 每次都刚好凑满门槛的规则。
        settled.Trigger = new() { Type = LiveEventType.Like, Threshold = 1, Counting = TriggerCounting.PerUser };
        var partial = ChatRule(); // 永远凑不满门槛、只留余数的规则。
        partial.Id = "partial"; partial.Trigger = new() { Type = LiveEventType.Like, Threshold = 1000, Counting = TriggerCounting.PerUser };
        for (var index = 0; index < 50010; index++) { engine.Evaluate(new() { Id = "l" + index, Type = LiveEventType.Like, Count = 1, UserId = "u" + index }, [settled, partial]); }
        Assert.True(engine.SaveState().Length < 5_000_000);
        Assert.True(engine.AccumulatedCount(partial.Id) < 50010);
    }

    /// <summary>“本次会话最多触发次数”在新会话清零，同会话内回滚恢复仍保留。</summary>
    [Fact]
    public void MaximumTriggersResetsForNewSession()
    {
        var rule = ChatRule(); rule.MaximumTriggers = 1; // 每场只触发一次的规则。
        var engine = new RuleEngine(reliable: true); // 上一场。
        Assert.Single(engine.Evaluate(Chat("s1", "回血"), [rule]));
        var saved = engine.SaveState(); // 持久化的规则状态。
        var restored = new RuleEngine(reliable: true); restored.LoadState(saved); // 同会话回滚恢复。
        Assert.Empty(restored.Evaluate(Chat("s2", "回血"), [rule]));
        var next = new RuleEngine(reliable: true); next.LoadState(saved); next.ResetSession(); // 程序重启后的新会话。
        Assert.Single(next.Evaluate(Chat("s3", "回血"), [rule]));
    }

    /// <summary>礼物触发的具名召唤必须填写名称；弹幕口令可以留空。</summary>
    [Fact]
    public void NamedSummonRequiresNameUnlessChatCommand()
    {
        var gift = ChatRule(); gift.Id = "g"; gift.Name = "礼物召唤"; // 礼物来源规则。
        gift.Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心" };
        gift.Root = new() { Kind = StepKind.Action, ActionId = "summon.named.ally", Arguments = new() { ["name"] = "" } };
        Assert.Contains(ProfileStore.ValidateRule(gift), error => error.Contains("准确怪物名", StringComparison.Ordinal));
        var command = ChatRule(); command.Id = "c"; command.Name = "弹幕召唤"; // 弹幕口令规则。
        command.Trigger = new() { Type = LiveEventType.Chat, TextMatching = TextMatching.StartsWith, Keywords = ["生成小弟"] };
        command.Root = new() { Kind = StepKind.Action, ActionId = "summon.named.ally", Arguments = new() { ["name"] = "" } };
        Assert.DoesNotContain(ProfileStore.ValidateRule(command), error => error.Contains("准确怪物名", StringComparison.Ordinal));
    }

    /// <summary>建立通用测试规则。</summary>
    /// <returns>已启用的规则。</returns>
    private static RuleDefinition ChatRule() => new() { Enabled = true, Trigger = new() { Type = LiveEventType.Chat, TextMatching = TextMatching.Exact, Keywords = ["回血"] } };

    /// <summary>建立弹幕事件。</summary>
    /// <param name="id">消息 ID。</param>
    /// <param name="text">弹幕文字。</param>
    /// <returns>输入事件。</returns>
    private static LiveEvent Chat(string id, string text) => new() { Id = id, Type = LiveEventType.Chat, Text = text };
}

/// <summary>验证礼物累计计数，防止将 1、2、3 连击错误相加成六个。</summary>
public sealed class EventNormalizerTests
{
    /// <summary>累计礼物只输出新增部分，并保留明确结束消息。</summary>
    [Fact]
    public void GiftComboUsesDeltaAndFinalTotal()
    {
        var normalizer = new EventNormalizer(); // 独立连击状态。
        Assert.Equal(1, normalizer.Normalize(Gift("g1", 1))?.Count);
        Assert.Equal(2, normalizer.Normalize(Gift("g2", 3))?.Count);
        var ended = Gift("g3", 3); // 最终结算不重复增加数量。
        ended.ComboEnded = true;
        var final = normalizer.Normalize(ended); // 输出结束语义。
        Assert.NotNull(final);
        Assert.Equal(0, final.Count);
        Assert.Equal(3, final.ComboTotal);
        Assert.Null(normalizer.Normalize(ended));
    }

    /// <summary>同一连击的不同消息 ID 结束包不得重复结算。</summary>
    [Fact]
    public void RepeatedComboEndIsNotSettledTwice()
    {
        var normalizer = new EventNormalizer(); // 独立礼物状态。
        normalizer.Normalize(Gift("r1", 3));
        var first = Gift("r2", 3); first.ComboEnded = true;
        Assert.NotNull(normalizer.Normalize(first));
        var duplicateEnd = Gift("r3", 3); duplicateEnd.ComboEnded = true;
        Assert.Null(normalizer.Normalize(duplicateEnd));
    }

    /// <summary>不同连击分组互不扣减。</summary>
    [Fact]
    public void GiftGroupsAreIndependent()
    {
        var normalizer = new EventNormalizer(); // 独立状态。
        normalizer.Normalize(Gift("g1", 3));
        var other = Gift("g2", 2); // 第二组礼物。
        other.ComboId = "other";
        Assert.Equal(2, normalizer.Normalize(other)?.Count);
    }

    /// <summary>建立累计礼物输入。</summary>
    /// <param name="id">消息 ID。</param>
    /// <param name="count">累计数量。</param>
    /// <returns>未归一化的礼物事件。</returns>
    private static LiveEvent Gift(string id, long count) => new() { Id = id, Type = LiveEventType.Gift, GiftId = "1", UserId = "42", ComboId = "combo", Count = count, IsCumulative = true };
}
