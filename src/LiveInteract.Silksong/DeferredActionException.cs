using System;

namespace LiveInteract.Silksong;

/// <summary>明确在修改游戏前拒绝，允许保留单位并等待条件，而不是消耗礼物报失败。</summary>
internal sealed class DeferredActionException : InvalidOperationException
{
    public string Code { get; }
    public DeferredActionException(string code, string message) : base(message) { Code = code; }
}
