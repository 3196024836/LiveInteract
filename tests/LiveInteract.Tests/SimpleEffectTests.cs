using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>验证具体效果及一份礼物一次效果的简明合同。</summary>
[Collection("Monster pools")]
public sealed class SimpleEffectTests
{
    /// <summary>怪物要以实际中文名字出现在功能选项，而不是让用户填内部 ID。</summary>
    [Fact]
    public void ConcreteMonstersHaveQuantityWithoutInternalIds()
    {
        var monsters = ActionCatalog.All.Where(item => item.Id.StartsWith("spawn.")).ToList();
        Assert.True(monsters.Count >= 25);
        Assert.Contains(monsters, item => item.Name == "生成颅潜虫");
        Assert.Contains(monsters, item => item.Name == "生成木蜂");
        Assert.Contains(monsters, item => item.Name.Contains("针姬"));
        foreach (var monster in monsters)
        {
            Assert.Contains(monster.Parameters, p => p.Key == "count" && p.Kind == ParameterKind.Integer);
            Assert.DoesNotContain(monster.Parameters, p => p.Key is "asset" or "fsm" or "event" or "enemy");
        }
    }

    /// <summary>每个小心心只触发独立效果一次，怪物数量留在效果参数中。</summary>
    [Fact]
    public async Task EachHeartTriggersOneSpawnWithConfiguredQuantity()
    {
        using var approved = new ApprovedSummonsScope(new[] { "spawn.bone-goomba" });
        var rule = new RuleDefinition
        {
            Name = "小心心生成怪物", Enabled = true,
            Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心", Threshold = 1 },
            Root = new() { ActionId = "spawn.bone-goomba", Arguments = new() { ["count"] = "3" } }
        };
        var engine = new RuleEngine();
        var adapter = new Adapter();
        var executor = new ActionExecutor(adapter);
        var first = new LiveEvent { Id = "heart1", Type = LiveEventType.Gift, GiftName = "小心心", Count = 1 };
        var match = Assert.Single(engine.Evaluate(first, [rule]));
        Assert.Equal(1, match.Count);
        Assert.True(await executor.ExecuteAsync(rule.Root, "test", false, default));
        Assert.Equal("3", Assert.Single(adapter.Requests).Arguments["count"]);
        var batch = new LiveEvent { Id = "heart2", Type = LiveEventType.Gift, GiftName = "小心心", Count = 2 };
        Assert.Equal(2, Assert.Single(engine.Evaluate(batch, [rule])).Count);
        Assert.Empty(engine.Evaluate(new() { Id = "other", Type = LiveEventType.Gift, GiftName = "玫瑰" }, [rule]));
    }

    /// <summary>回血、Boss 回血和具名丝技各自独立，参数不是固定值。</summary>
    [Fact]
    public void HealingAndSkillsAreIndependentEffects()
    {
        Assert.Single(ActionCatalog.All.Single(item => item.Id == "boss.heal").Parameters, p => p.Key == "value");
        foreach (var value in new[] { "1", "4", "9" })
        {
            Assert.Empty(ActionValidation.Validate(new() { ActionId = "health.add", Arguments = new() { ["value"] = value } }));
        }
        Assert.Empty(ActionValidation.Validate(new() { ActionId = "boss.heal", Arguments = new() { ["value"] = "300" } }));
        Assert.Equal(6, ActionCatalog.All.Count(item => item.Id.StartsWith("cast.")));
        Assert.All(ActionCatalog.All.Where(item => item.Id.StartsWith("cast.")), item => Assert.Empty(item.Parameters));
        Assert.NotEmpty(ActionValidation.Validate(new() { ActionId = "spawn.bone-goomba", Arguments = new() { ["count"] = "0" } }));
        Assert.NotEmpty(ActionValidation.Validate(new() { ActionId = "spawn.bone-goomba", Arguments = new() { ["count"] = "21" } }));
    }

    /// <summary>旧组合仍能原样读回，不被自动截成第一个动作。</summary>
    [Fact]
    public void ExistingCompositeProfileIsPreserved()
    {
        var original = new RuleProfile { Rules = [new() { Name = "旧组合", Root = new() { Kind = StepKind.Sequence, Children = [new() { ActionId = "health.add", Arguments = new() { ["value"] = "4" } }, new() { ActionId = "silk.full" }] } }] };
        var restored = ProfileStore.Read(Newtonsoft.Json.JsonConvert.SerializeObject(original), false);
        Assert.Equal(StepKind.Sequence, restored.Rules[0].Root.Kind);
        Assert.Equal(2, restored.Rules[0].Root.Children.Count);
        Assert.Equal("4", restored.Rules[0].Root.Children[0].Arguments["value"]);
    }

    /// <summary>仅记录请求的构造端，不连接实际游戏。</summary>
    private sealed class Adapter : IGameAdapter
    {
        public bool IsConnected => true;
        public GameStatus Status => new() { CanAct = true };
        public event Action<GameStatus>? StatusChanged { add { } remove { } }
        public List<ActionRequest> Requests { get; } = [];
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(new ActionResult { RequestId = request.Id, Success = true });
        }
        public Task ResetAsync(CancellationToken token) => Task.CompletedTask;
    }
}
