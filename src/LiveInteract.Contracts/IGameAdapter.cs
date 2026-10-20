using System;
using System.Threading;
using System.Threading.Tasks;

namespace LiveInteract.Contracts;

/// <summary>游戏适配器的最小执行接口，不包含平台接入或界面逻辑。</summary>
public interface IGameAdapter
{
    /// <summary>是否拥有近期有效的游戏心跳。</summary>
    bool IsConnected { get; }
    /// <summary>最近收到的游戏状态。</summary>
    GameStatus? Status { get; }
    /// <summary>游戏状态发生变化。</summary>
    event Action<GameStatus>? StatusChanged;

    /// <summary>执行指令并等待游戏回执，超时不自动重发。</summary>
    /// <param name="request">通过校验的指令。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>游戏回执或明确的失败原因。</returns>
    Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken);

    /// <summary>取消待执行指令并恢复临时效果。</summary>
    /// <param name="cancellationToken">取消信号。</param>
    /// <returns>操作完成任务。</returns>
    Task ResetAsync(CancellationToken cancellationToken);
}
