using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>验证执行树不重放部分成功动作并正确取消。</summary>
public sealed class ExecutionTests
{
    /// <summary>顺序组合在失败后停止，已成功动作不重放。</summary>
    [Fact]
    public async Task SequenceStopsOnFailureWithoutReplay()
    {
        var adapter = new FakeAdapter { FailOn = "silk.full" }; // 第二步失败。
        var executor = new ActionExecutor(adapter); // 被测调度器。
        var root = new ActionStep { Kind = StepKind.Sequence, Children = [new() { ActionId = "health.full" }, new() { ActionId = "silk.full" }, new() { ActionId = "tools.refill" }] }; // 三步组合。
        Assert.False(await executor.ExecuteAsync(root, "test", false, default));
        Assert.Equal(new[] { "health.full", "silk.full" }, adapter.Calls);
    }

    /// <summary>重复和并行动作均被执行，指令 ID 不重复。</summary>
    [Fact]
    public async Task RepeatProducesUniqueCommandIds()
    {
        var adapter = new FakeAdapter(); // 成功游戏端。
        var executor = new ActionExecutor(adapter); // 被测调度器。
        Assert.True(await executor.ExecuteAsync(new() { Kind = StepKind.Repeat, RepeatCount = 3, Children = [new() { ActionId = "health.full" }] }, "test", false, default));
        Assert.Equal(3, adapter.Ids.Distinct().Count());
    }

    /// <summary>取消等待后不执行后续游戏动作。</summary>
    [Fact]
    public async Task CancellationStopsDelayedActions()
    {
        var adapter = new FakeAdapter(); // 成功游戏端。
        var executor = new ActionExecutor(adapter); // 被测执行器。
        using var cancel = new CancellationTokenSource(25); // 取消等待。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(new() { Kind = StepKind.Sequence, Children = [new() { Kind = StepKind.Delay, DelaySeconds = 10 }, new() { ActionId = "health.full" }] }, "test", false, cancel.Token));
        Assert.Empty(adapter.Calls);
    }

    /// <summary>验证拒绝动作不能到达游戏端。</summary>
    [Fact]
    public async Task PersistentActionIsNotSentWithoutApproval()
    {
        var adapter = new FakeAdapter(); // 记录发送。
        Assert.False(await new ActionExecutor(adapter).ExecuteAsync(new() { ActionId = "currency.rosaries" }, "test", false, default));
        Assert.Empty(adapter.Calls);
    }

    /// <summary>观众数量映射到参数时不需要脚本。</summary>
    [Fact]
    public async Task QuantityBindingResolvesAtExecution()
    {
        var adapter = new FakeAdapter(); // 记录真实发送参数。
        var step = new ActionStep { ActionId = "health.add", ParameterBindings = new() { ["value"] = new() { Source = ParameterValueSource.EventCount, Multiplier = 2, Offset = 1 } } }; // 3 个单位映射为 7。
        Assert.True(await new ActionExecutor(adapter).ExecuteAsync(step, "test", false, default, new() { EventCount = 3 }));
        Assert.Equal("7", Assert.Single(adapter.Requests).Arguments["value"]);
    }

    /// <summary>缺少上下文时不得假定礼物数量为一。</summary>
    [Fact]
    public async Task QuantityBindingRequiresEventContext()
    {
        var adapter = new FakeAdapter(); // 没有观众事件。
        var step = new ActionStep { ActionId = "health.add", ParameterBindings = new() { ["value"] = new() { Source = ParameterValueSource.EventCount } } };
        Assert.False(await new ActionExecutor(adapter).ExecuteAsync(step, "test", false, default));
        Assert.Empty(adapter.Calls);
    }

    /// <summary>只记录收到的命令，不模拟任何实际游戏效果。</summary>
    private sealed class FakeAdapter : IGameAdapter
    {
        /// <summary>构造端始终连接。</summary>
        public bool IsConnected => true;
        /// <summary>没有伪造实际游戏心跳。</summary>
        public GameStatus? Status => null;
        /// <summary>接口所需事件。</summary>
        public event Action<GameStatus>? StatusChanged { add { } remove { } }
        /// <summary>收到的动作。</summary>
        public List<string> Calls { get; } = [];
        /// <summary>收到的指令标识。</summary>
        public List<string> Ids { get; } = [];
        /// <summary>全部构造请求。</summary>
        public List<ActionRequest> Requests { get; } = [];
        /// <summary>要故意失败的动作。</summary>
        public string FailOn { get; set; } = "";

        /// <summary>记录请求并返回构造回执。</summary>
        /// <param name="request">请求。</param>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>构造结果。</returns>
        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Calls.Add(request.ActionId);
            Ids.Add(request.Id);
            return Task.FromResult(new ActionResult { RequestId = request.Id, Success = request.ActionId != FailOn, Message = "test" });
        }

        /// <summary>测试端没有游戏效果需要恢复。</summary>
        /// <param name="cancellationToken">取消信号。</param>
        /// <returns>完成任务。</returns>
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
