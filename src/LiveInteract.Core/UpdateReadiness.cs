namespace LiveInteract.Core;

/// <summary>更新的重启条件集中判断，不允许用更新打断直播收包或正在处理的队列。</summary>
public static class UpdateReadiness
{
    public static string? BlockReason(bool sourceOpen, bool enabled, bool gameRunning,
        long pendingJobs, long pendingFrames, long pendingEvents, bool maintenanceBusy)
    {
        if (sourceOpen) { return "请先关闭直播接入窗口，避免更新重启期间漏收互动。"; }
        if (enabled) { return "请先暂停新触发，再安装更新。"; }
        if (pendingJobs > 0 || pendingFrames > 0 || pendingEvents > 0)
        {
            return "还有未完成的互动，请处理或明确取消队列后再更新；更新不会清空礼物记录。";
        }
        if (gameRunning) { return "请先正常退出游戏；Mod 只能在游戏退出后更新。"; }
        if (maintenanceBusy) { return "正在维护游戏，请完成后再更新。"; }
        return null;
    }
}
