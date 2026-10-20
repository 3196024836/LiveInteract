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
