using LiveInteract.Contracts;

namespace LiveInteract.Core;

/// <summary>执行无脚本动作树，逐动作报告结果；失败后不重放已成功步骤。</summary>
public sealed class ActionExecutor
{
    // 游戏适配器和有界的并行动作槽。
    private readonly IGameAdapter adapter;
    private readonly Func<bool>? persistentPermission;
    private readonly SemaphoreSlim actionSlots = new(4, 4);

    /// <summary>每次完成一个游戏动作后报告请求与实际回执。</summary>
    public event Action<ActionRequest, ActionResult>? ActionCompleted;
    /// <summary>发送前先记录意图，断线时可以核对而不谎报成功。</summary>
    public event Action<ActionRequest>? ActionStarted;

    /// <summary>建立执行器。</summary>
    /// <param name="adapter">游戏执行端。</param>
    /// <param name="persistentPermission">每个动作执行时重新读取的会话许可。</param>
    public ActionExecutor(IGameAdapter adapter, Func<bool>? persistentPermission = null) { this.adapter = adapter; this.persistentPermission = persistentPermission; }

    /// <summary>执行动作树；取消将向上传播，不把取消伪装成成功。</summary>
    /// <param name="step">根节点。</param>
    /// <param name="displayText">游戏提示。</param>
    /// <param name="allowPersistent">是否已确认永久修改。</param>
    /// <param name="token">取消信号。</param>
    /// <param name="context">可选观众数量上下文。</param>
    /// <returns>所有已选步骤成功时返回真。</returns>
    public async Task<bool> ExecuteAsync(ActionStep step, string displayText, bool allowPersistent, CancellationToken token, ActionContext? context = null)
    {
        token.ThrowIfCancellationRequested();
        switch (step.Kind)
        {
            case StepKind.Delay:
                await Task.Delay(TimeSpan.FromSeconds(step.DelaySeconds), token);
                return true;
            case StepKind.Sequence:
            case StepKind.Repeat:
                for (var repeat = 0; repeat < (step.Kind == StepKind.Repeat ? step.RepeatCount : 1); repeat++)
                {
                    foreach (var child in step.Children)
                    {
                        if (!await ExecuteAsync(child, displayText, allowPersistent, token, context))
                        {
                            return false;
                        }
                    }
                }
                return true;
            case StepKind.Parallel:
                var results = await Task.WhenAll(step.Children.Select(child => ExecuteAsync(child, displayText, allowPersistent, token, context))); // 保留各自结果。
                return results.All(value => value);
            case StepKind.Random:
                var total = step.Children.Sum(child => child.Weight); // 仅在当前容器内计算权重。
                if (total <= 0)
                {
                    return false;
                }
                var cursor = Random.Shared.NextDouble() * total; // 本次随机分支。
                foreach (var child in step.Children)
                {
                    cursor -= child.Weight;
                    if (cursor < 0)
                    {
                        return await ExecuteAsync(child, displayText, allowPersistent, token, context);
                    }
                }
                return false;
            default:
                var request = new ActionRequest // 每次执行都创建新的去重标识。
                {
                    Id = context?.NextCommandId() ?? Guid.NewGuid().ToString("N"), JobId = context?.JobId ?? "", UnitIndex = context?.UnitIndex ?? 0, Triggerer = context?.Triggerer ?? "主播", SourceType = context?.SourceType,
                    ActionId = step.ActionId, Arguments = new(step.Arguments), DurationSeconds = step.DurationSeconds,
                    Stacking = step.Stacking, AllowPersistent = allowPersistent && (persistentPermission?.Invoke() ?? true), DisplayText = displayText
                };
                if (step.ActionId is "summon.named.enemy" or "summon.named.ally" && context?.SourceType == LiveEventType.Chat)
                {
                    try { request.Arguments["name"] = SummonCommands.Extract(context.InputText, step.ActionId == "summon.named.enemy" ? "生成怪物" : "生成小弟"); }
                    catch (ArgumentException error) { return Reject(request, error.Message); }
                }
                foreach (var pair in step.ParameterBindings)
                {
                    if (context == null) { return Reject(request, "此动作使用观众数量联动，请从模拟输入或直播事件测试。"); }
                    var parameter = ActionCatalog.All.FirstOrDefault(item => item.Id == step.ActionId)?.Parameters.FirstOrDefault(item => item.Key == pair.Key); // 固定参数定义。
                    if (parameter == null || parameter.Kind is not (ParameterKind.Number or ParameterKind.Integer)) { return Reject(request, "数量联动只能作用于此动作的数值参数。"); }
                    var source = pair.Value.Source switch { ParameterValueSource.EventCount => (double)context.EventCount, ParameterValueSource.ComboTotal => context.ComboTotal, ParameterValueSource.TriggerIndex => context.TriggerIndex, _ => double.NaN }; // 无脚本来源。
                    var value = source * pair.Value.Multiplier + pair.Value.Offset; // 线性映射。
                    if (!double.IsFinite(value)) { return Reject(request, "数量联动产生了无效数值。"); }
                    value = Math.Clamp(value, parameter.Minimum, parameter.Maximum);
                    if (parameter.Kind == ParameterKind.Integer) { value = Math.Round(value); }
                    request.Arguments[pair.Key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var errors = ActionValidation.Validate(request); // 边界校验。
                if (errors.Count > 0)
                {
                    var rejected = new ActionResult { RequestId = request.Id, Message = string.Join("；", errors) }; // 未发送的失败。
                    ActionCompleted?.Invoke(request, rejected);
                    return false;
                }
                ActionValidation.FillDefaults(request);
                await actionSlots.WaitAsync(token);
                try
                {
                    request.EffectText = ActionPresentation.Describe(request);
                    ActionStarted?.Invoke(request);
                    var result = await adapter.ExecuteAsync(request, token); // 实际游戏回执。
                    ActionCompleted?.Invoke(request, result);
                    return result.Success;
                }
                finally
                {
                    actionSlots.Release();
                }
        }
    }
    /// <summary>报告尚未发往游戏的失败。</summary>
    /// <param name="request">准备执行的指令。</param>
    /// <param name="message">失败原因。</param>
    /// <returns>始终为假。</returns>
    private bool Reject(ActionRequest request, string message)
    {
        ActionCompleted?.Invoke(request, new() { RequestId = request.Id, Message = message });
        return false;
    }

}
