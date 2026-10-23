using System.IO;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>保留原辅助入口，实际替换与回滚统一由可独立测试的 Core 执行。</summary>
public static class UpdateApplier
{
    public static string Apply(string staging, string install, int waitProcessId, out string? error)
    {
        var result = UpdateInstaller.Apply(staging, install, waitProcessId);
        error = result.Error;
        return result.CanRestart ? result.Executable : "";
    }

    /// <summary>只清理由本工具建立的独立暂存目录；失败现场和备份均保留。</summary>
    public static void Cleanup(string staging)
    {
        var absolute = Path.GetFullPath(staging);
        if (!Path.GetFileName(absolute).StartsWith("stage-", StringComparison.Ordinal) ||
            Path.GetFileName(Path.GetDirectoryName(absolute)) != "update") { return; }
        try
        {
            UpdatePackage.RequireRegularPath(absolute);
            if (Directory.Exists(absolute)) { Directory.Delete(absolute, true); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}
